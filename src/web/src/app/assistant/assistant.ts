import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken, signal } from '@angular/core';
import { Router } from '@angular/router';
import type { AgentSubscriber, HttpAgent } from '@ag-ui/client';
import { firstValueFrom } from 'rxjs';
import { RUNTIME_CONFIG } from '../config';
import { MapView } from '../map/map-view';
import { SiteQueryResult } from '../query/query-model';
import { Tools } from '../shell/tools';
import { LotsAuth } from './lots-auth';

/** The MCP tool a chat reacts to (#330). It is a normal tool in Lots: policy and audit are Lots', the drawing is ours. */
const SHOW_ON_MAP = 'show_on_map';

type Agent = Pick<HttpAgent, 'headers' | 'runAgent' | 'setMessages'>;

/** The AG-UI client is a chunk of its own, loaded only when someone asks something. */
export const AGENT_FACTORY = new InjectionToken<(url: string, threadId: string) => Promise<Agent>>(
  'AG-UI agent',
  {
    providedIn: 'root',
    factory: () => async (url, threadId) => {
      const { HttpAgent } = await import('@ag-ui/client');
      return new HttpAgent({ url, threadId });
    },
  },
);

export interface ChatLine {
  id: string;
  role: 'user' | 'assistant' | 'notice' | 'error';
  text: string;
  /** Where the person goes on, in Lots. */
  link?: { href: string; label: string };
}

/** What a request to /api/query/sites may hold, and nothing else a model wrote. */
export function siteQueryFrom(args: unknown): Record<string, unknown> {
  const a = (args ?? {}) as Record<string, unknown>;
  const strings = (v: unknown) =>
    Array.isArray(v) && v.every((s) => typeof s === 'string') && v.length > 0 ? v : undefined;
  const objects = (v: unknown) =>
    Array.isArray(v) && v.every((o) => typeof o === 'object' && o !== null) && v.length > 0
      ? v
      : undefined;
  return {
    limit: 1, // every match's position comes with the answer; the map needs no list
    siteTypes: strings(a['siteTypes']),
    lifecycles: strings(a['lifecycles']),
    serviceTypes: strings(a['serviceTypes']),
    equipment: objects(a['equipment']),
    siteAttributes: objects(a['siteAttributes']),
    place: typeof a['place'] === 'string' ? a['place'] : undefined,
  };
}

function plural(total: number): string {
  return total === 1 ? '1 site' : `${total.toLocaleString('sv-SE')} siter`;
}

/**
 * A chat with the person's own context in Lots, over AG-UI (ADR-0023, Lots ADR 0067). The browser talks to Lots directly
 * with the person's Lots token, so the run is theirs. What the agent shows comes from our own API with their cmdb token: a
 * tool call's arguments are a question, never an answer, and the map shows what the person's scope gives.
 */
@Injectable({ providedIn: 'root' })
export class Assistant {
  private readonly config = inject(RUNTIME_CONFIG, { optional: true });
  private readonly lots = inject(LotsAuth);
  private readonly factory = inject(AGENT_FACTORY);
  private readonly http = inject(HttpClient);
  private readonly mapView = inject(MapView);
  private readonly tools = inject(Tools);
  private readonly router = inject(Router);

  private readonly url = (this.config?.lotsUrl ?? '').replace(/\/+$/, '');
  private readonly threadId = crypto.randomUUID();
  private agent: Agent | null = null;
  private seq = 0;

  readonly context = this.config?.lotsContext ?? 'cmdb';
  readonly enabled =
    this.url.length > 0 && !!this.config?.lotsAuthority && !!this.config?.lotsClientId;
  readonly open = signal(false);
  readonly lines = signal<readonly ChatLine[]>([]);
  readonly busy = signal(false);

  /** From a click, so the sign-in popup is allowed to open. */
  toggle(): void {
    this.open.update((o) => !o);
    if (this.open() && this.lots.access() === 'signed-out') {
      void this.lots.signIn();
    }
  }

  async ask(question: string): Promise<void> {
    const text = question.trim();
    if (!text || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.add({ role: 'user', text });
    try {
      const token = await this.lots.token();
      if (!token) {
        this.add({
          role: 'notice',
          text: 'Inloggningen i Lots har gått ut. Logga in igen och fråga på nytt.',
        });
        return;
      }
      const agent = (this.agent ??= await this.factory(`${this.url}/ag-ui`, this.threadId));
      agent.headers = { Authorization: `Bearer ${token}` };
      // Lots holds the earlier turns of the thread itself and reads only this one.
      agent.setMessages([{ id: `user-${++this.seq}`, role: 'user', content: text }]);
      await agent.runAgent({ forwardedProps: { context: this.context } }, this.subscriber());
    } catch (e: unknown) {
      this.add({ role: 'error', text: this.problem(e) });
    } finally {
      this.busy.set(false);
    }
  }

  private subscriber(): AgentSubscriber {
    const asked = new Map<string, unknown>();
    const lineOf = new Map<string, string>();
    return {
      onTextMessageContentEvent: ({ event }) => {
        const id = lineOf.get(event.messageId);
        if (id) {
          this.lines.update((all) =>
            all.map((l) => (l.id === id ? { ...l, text: l.text + event.delta } : l)),
          );
        } else {
          lineOf.set(event.messageId, this.add({ role: 'assistant', text: event.delta }));
        }
      },
      onToolCallEndEvent: ({ event, toolCallName, toolCallArgs }) => {
        if (toolCallName === SHOW_ON_MAP) {
          asked.set(event.toolCallId, toolCallArgs);
        }
      },
      onToolCallResultEvent: ({ event }) => {
        const args = asked.get(event.toolCallId);
        // A refused or failed call has no result to show, and that is the agent's to explain.
        if (args !== undefined && this.answered(event.content)) {
          void this.show(args);
        }
      },
      onCustomEvent: ({ event }) => this.custom(event.name, event.value),
      onRunErrorEvent: ({ event }) => {
        this.add({ role: 'error', text: event.message || 'Körningen i Lots misslyckades.' });
      },
    };
  }

  private answered(content: unknown): boolean {
    try {
      return typeof (JSON.parse(String(content)) as { total?: unknown }).total === 'number';
    } catch {
      return false;
    }
  }

  /** Marks what the question gives the person, from our own API under their own scope, and brings the map into view. */
  private async show(args: unknown): Promise<void> {
    try {
      const result = await firstValueFrom(
        this.http.post<SiteQueryResult>('/api/query/sites', siteQueryFrom(args)),
      );
      if (this.router.url.startsWith('/graf')) {
        await this.router.navigate(['/'], { queryParamsHandling: 'preserve' });
      }
      if (this.tools.open() && this.tools.size() === 'workspace') {
        this.tools.close();
      }
      this.mapView.mark(result.points, result.extent);
      this.add({
        role: 'notice',
        text:
          result.total === 0
            ? 'Inga siter att markera på kartan.'
            : result.points.length === 0
              ? `${plural(result.total)} hittades, men din behörighet döljer var de ligger.`
              : result.total > result.points.length
                ? `${result.points.length.toLocaleString('sv-SE')} av ${plural(result.total)} markerade på kartan.`
                : `${plural(result.total)} markerade på kartan.`,
      });
    } catch (e: unknown) {
      const errors = (e as { error?: { errors?: Record<string, string[]> } }).error?.errors;
      this.add({
        role: 'error',
        text: errors
          ? Object.values(errors).flat().join(' ')
          : 'Kunde inte markera siterna på kartan.',
      });
    }
  }

  private custom(name: string, value: unknown): void {
    const v = (value ?? {}) as { url?: unknown; servers?: unknown };
    const link = (label: string) => this.link(v.url, label);
    if (name === 'lots.connect') {
      this.add({
        role: 'notice',
        text: 'Kontexten behöver ditt eget konto, så att svaren följer din behörighet. Koppla kontot i Lots en gång och fråga sedan igen.',
        link: link('Koppla kontot i Lots'),
      });
    } else if (name === 'lots.approval') {
      this.add({
        role: 'notice',
        text: 'Det här behöver ett godkännande. Körningen fortsätter i Lots.',
        link: link('Öppna i Lots'),
      });
    } else if (name === 'lots.running') {
      this.add({
        role: 'notice',
        text: 'Det tar tid. Körningen fortsätter i Lots.',
        link: link('Följ i Lots'),
      });
    }
  }

  /** Only a link into the Lots we are configured with. */
  private link(url: unknown, label: string): ChatLine['link'] {
    if (typeof url !== 'string') {
      return undefined;
    }
    try {
      const target = new URL(url, `${this.url}/`);
      return target.origin === new URL(this.url).origin ? { href: target.href, label } : undefined;
    } catch {
      return undefined;
    }
  }

  private problem(e: unknown): string {
    const status = (e as { status?: number }).status;
    const message = (e as { message?: string }).message ?? '';
    const has = (code: number) =>
      status === code || message.includes(`HTTP ${code}`) || message.includes(`${code}:`);
    if (has(401)) {
      return 'Lots godtog inte inloggningen. Logga in igen.';
    }
    if (has(403)) {
      return `Du har inte tillgång till kontexten ${this.context} i Lots.`;
    }
    if (has(429)) {
      return 'Lots har mycket att göra just nu. Försök igen om en stund.';
    }
    return 'Kunde inte nå Lots. Kontrollera anslutningen och försök igen.';
  }

  private add(line: Omit<ChatLine, 'id'>): string {
    const id = `line-${++this.seq}`;
    this.lines.update((all) => [...all, { ...line, id }]);
    return id;
  }
}
