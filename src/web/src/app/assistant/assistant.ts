import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken, signal } from '@angular/core';
import { Router } from '@angular/router';
import type { AgentSubscriber, HttpAgent } from '@ag-ui/client';
import { firstValueFrom } from 'rxjs';
import { RUNTIME_CONFIG } from '../config';
import { Selection } from '../grid/selection';
import { MapView } from '../map/map-view';
import { PlanDetail } from '../plans/plan-model';
import { SiteQueryResult } from '../query/query-model';
import { Tools } from '../shell/tools';
import { cardFor, ChatCard } from './assistant-cards';
import { LotsAuth } from './lots-auth';

/** The MCP tools a chat reacts to (#330, #334, #335). They are normal tools in Lots: policy and audit are Lots', the drawing is ours. */
const SHOW_ON_MAP = 'show_on_map';
const OPEN_OBJECT = 'open_object';
const REQUEST_CHANGE = 'request_change';
const OBJECT_REF = /^(site|equipment|cable|service|circuit|route-segment|service-point):\d+$/;
const PLAN_REF = /^plan:(\d+)$/;

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
  role: 'user' | 'assistant' | 'notice' | 'error' | 'card' | 'change';
  text: string;
  /** Where the person goes on, in Lots. */
  link?: { href: string; label: string };
  card?: ChatCard;
  change?: ChangeRequest;
}

/** A question to the person before anything is written (#335). Everything in it comes from the plan in cmdb, not from what the model wrote. */
export interface ChangeRequest {
  planId: number;
  changes: readonly string[];
  /** What stops it. With any, it cannot be approved. */
  problems: readonly string[];
  state: 'asking' | 'writing' | 'written' | 'declined' | 'failed';
  message?: string;
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
    withinKm: typeof a['withinKm'] === 'number' ? a['withinKm'] : undefined,
    extent:
      Array.isArray(a['extent']) && a['extent'].every((n) => typeof n === 'number')
        ? a['extent']
        : undefined,
  };
}

const text = (v: unknown): string => (typeof v === 'string' ? v : '');

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
  private readonly selection = inject(Selection);

  private readonly url = (this.config?.lotsUrl ?? '').replace(/\/+$/, '');
  private readonly threadId = crypto.randomUUID();
  private agent: Agent | null = null;
  private seq = 0;
  /** What the person answered since the last question, so the agent knows how a change request ended. */
  private answers: string[] = [];

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
    // An open change request is answered here, in the browser. It never goes through the model.
    const asking = this.lines().find((l) => l.change?.state === 'asking');
    if (asking && /^(ja|nej)[.!]?$/i.test(text)) {
      this.add({ role: 'user', text });
      await this.answer(asking.id, /^ja/i.test(text));
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
      const context = this.screen();
      this.answers = [];
      await agent.runAgent(
        { forwardedProps: { context: this.context }, context },
        this.subscriber(),
      );
    } catch (e: unknown) {
      this.add({ role: 'error', text: this.problem(e) });
    } finally {
      this.busy.set(false);
    }
  }

  /** Answers a change request: the person's own decision, sent to cmdb with their own token. */
  async answer(lineId: string, yes: boolean): Promise<void> {
    const change = this.lines().find((l) => l.id === lineId)?.change;
    if (change?.state !== 'asking' || (yes && change.problems.length > 0)) {
      return;
    }
    const set = (next: Partial<ChangeRequest>) =>
      this.lines.update((all) =>
        all.map((l) =>
          l.id === lineId && l.change ? { ...l, change: { ...l.change, ...next } } : l,
        ),
      );
    set({ state: 'writing' });
    try {
      await firstValueFrom(
        this.http.post(`/api/plans/${change.planId}/${yes ? 'apply' : 'cancel'}`, {}),
      );
      set({ state: yes ? 'written' : 'declined' });
      this.answers.push(
        `The person answered ${yes ? 'yes' : 'no'} to change request plan:${change.planId}: ${yes ? 'it is written' : 'nothing was written'}.`,
      );
    } catch (e: unknown) {
      const message = (e as { error?: { detail?: string; message?: string } }).error;
      set({
        state: 'failed',
        message: message?.detail ?? message?.message ?? 'Ändringen kunde inte skrivas.',
      });
    }
  }

  /** Opens an object's panel and takes the map there. */
  openObject(ref: string): void {
    if (!OBJECT_REF.test(ref)) {
      return;
    }
    void this.router.navigate(['/'], {
      queryParams: { p: ref },
      queryParamsHandling: 'merge',
    });
  }

  /** What the person is looking at, as data for the agent: "this site" and "here" mean something then. Nothing a page wrote. */
  private screen(): { description: string; value: string }[] {
    const items: { description: string; value: string }[] = [];
    const open = this.router.parseUrl(this.router.url).queryParamMap.get('p')?.split(',').at(-1);
    if (open && OBJECT_REF.test(open)) {
      items.push({ description: 'Object open in the panel', value: open });
    }
    const extent = this.mapView.extent();
    if (extent?.length === 4) {
      items.push({
        description: 'Map view, [minX, minY, maxX, maxY] in EPSG:3006',
        value: `[${extent.map((n) => Math.round(n)).join(', ')}]`,
      });
    }
    const marked = this.mapView.highlight()?.points.length;
    if (marked) {
      items.push({ description: 'Sites marked on the map', value: String(marked) });
    }
    const selected = this.selection.current();
    if (selected) {
      items.push({
        description: 'Selected on the map',
        value: `${selected.siteIds
          .slice(0, 20)
          .map((id) => `site:${id}`)
          .join(
            ', ',
          )}${selected.siteIds.length > 20 ? ` and ${selected.siteIds.length - 20} more` : ''}`,
      });
    }
    for (const answer of this.answers) {
      items.push({ description: 'Since the last question', value: answer });
    }
    return items;
  }

  private subscriber(): AgentSubscriber {
    const asked = new Map<string, { name: string; args: unknown }>();
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
        asked.set(event.toolCallId, { name: toolCallName, args: toolCallArgs });
      },
      onToolCallResultEvent: ({ event }) => {
        const call = asked.get(event.toolCallId);
        if (!call) {
          return;
        }
        // A refused or failed call has no result to show, and that is the agent's to explain.
        const card = cardFor(call.name, event.content);
        if (card) {
          this.add({ role: 'card', text: '', card });
        }
        if (call.name === SHOW_ON_MAP && this.answered(event.content)) {
          void this.show(call.args);
        } else if (call.name === OPEN_OBJECT) {
          this.openObject(this.field(event.content, 'ref'));
        } else if (call.name === REQUEST_CHANGE) {
          void this.askAboutChange(event.content);
        }
      },
      onCustomEvent: ({ event }) => this.custom(event.name, event.value),
      onRunErrorEvent: ({ event }) => {
        this.add({ role: 'error', text: event.message || 'Körningen i Lots misslyckades.' });
      },
    };
  }

  /** Reads the plan from cmdb and asks. Only a draft of the person's own comes this far, whatever the call claimed. */
  private async askAboutChange(content: unknown): Promise<void> {
    let plan = '';
    try {
      plan = text((JSON.parse(String(content)) as { plan?: { ref?: unknown } }).plan?.ref);
    } catch {
      return; // a refusal, which the agent explains
    }
    const id = PLAN_REF.exec(plan)?.[1];
    if (!id) {
      return;
    }
    try {
      const [detail, me] = await Promise.all([
        firstValueFrom(this.http.get<PlanDetail>(`/api/plans/${id}`)),
        firstValueFrom(this.http.get<{ username: string }>('/api/me')),
      ]);
      if (detail.plan.status !== 'draft' || detail.plan.createdBy !== me.username) {
        this.add({
          role: 'error',
          text: 'Ändringsbegäran går inte att besvara här: planen är inte ett utkast av dig.',
        });
        return;
      }
      this.add({
        role: 'change',
        text: '',
        change: {
          planId: detail.plan.id,
          changes: detail.operations.map((o) => o.summary),
          problems: detail.operations.flatMap((o) => (o.problem ? [o.problem] : [])),
          state: 'asking',
        },
      });
    } catch {
      this.add({ role: 'error', text: 'Kunde inte läsa ändringsbegäran från cmdb.' });
    }
  }

  private field(content: unknown, key: string): string {
    try {
      return text((JSON.parse(String(content)) as Record<string, unknown>)[key]);
    } catch {
      return '';
    }
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
