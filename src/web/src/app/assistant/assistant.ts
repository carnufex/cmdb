import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken, signal } from '@angular/core';
import { Router } from '@angular/router';
import type { AgentSubscriber, HttpAgent } from '@ag-ui/client';
import { firstValueFrom } from 'rxjs';
import { RUNTIME_CONFIG } from '../config';
import { Selection } from '../grid/selection';
import { MapView } from '../map/map-view';
import { PlanDetail } from '../plans/plan-model';
import { SiteQueryHit, SiteQueryResult } from '../query/query-model';
import { Tools } from '../shell/tools';
import { answerOf, cardFor, ChatCard, stepLabel } from './assistant-cards';
import type { Lexer } from './assistant-markdown';
import { LotsAuth } from './lots-auth';

/** The MCP tools a chat reacts to (#330, #334, #335). They are normal tools in Lots: policy and audit are Lots', the drawing is ours. */
const SHOW_ON_MAP = 'show_on_map';
const FIND_SITES = 'find_sites';
const OPEN_OBJECT = 'open_object';
const REQUEST_CHANGE = 'request_change';
const OBJECT_REF = /^(site|equipment|cable|service|circuit|route-segment|service-point):\d+$/;
const PLAN_REF = /^plan:(\d+)$/;
/** As many as the list holds; the API's own limit. The map gets every position up to its own limit. */
const LISTED_SITES = 1000;
/** Every match on the map, as clusters: more than the whole network holds. */
const MAPPED_SITES = 50_000;

type Agent = Pick<HttpAgent, 'headers' | 'runAgent' | 'setMessages' | 'abortRun'>;

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

/** Takes the person through the provider in a hidden frame, in the session they already have. */
export const HIDDEN_FRAME = new InjectionToken<(url: string) => () => void>('Hidden frame', {
  providedIn: 'root',
  factory: () => (url) => {
    const frame = document.createElement('iframe');
    frame.hidden = true;
    frame.src = url;
    document.body.append(frame);
    return () => frame.remove();
  },
});

/** A chat from earlier, as the list of them shows it. */
export interface ChatThread {
  id: string;
  title: string;
  updatedAt: string;
}

interface ThreadStep {
  name: string;
  arguments?: string | null;
  result?: string | null;
}

interface ThreadTurn {
  question: string;
  status: string;
  answer?: string | null;
  steps: ThreadStep[];
}

/** How long a connection in the hidden frame gets, as the waits before asking again. */
const CONNECT_WAITS_MS = [1500, 2500, 4000];

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** So is the markdown parser. */
export const MARKDOWN_LEXER = new InjectionToken<() => Promise<Lexer>>('Markdown lexer', {
  providedIn: 'root',
  factory: () => async () => {
    const { marked } = await import('marked');
    return (text) => marked.lexer(text, { gfm: true, breaks: true });
  },
});

/** A list of sites from our own API under the person's own access. */
export interface SiteList {
  rows: readonly SiteQueryHit[];
  total: number;
  /** What marking them takes. */
  points: SiteQueryResult['points'];
  extent: SiteQueryResult['extent'];
  /** The person's access hides where the sites lie. */
  positionsHidden: boolean;
}

/** One thing the agent did: a line in the chat that opens on what came back. */
export interface ToolStep {
  id: string;
  tool: string;
  label: string;
  state: 'running' | 'done' | 'failed';
  /** The number the agent cites it by, as [t1]. */
  source?: number;
  /** The question it answers: what its layer on the map is called. */
  question: string;
  args?: unknown;
  card?: ChatCard;
  sites?: SiteList;
  /** What went wrong: a refusal, as the tool said it. */
  problem?: string;
  open: boolean;
  loading?: boolean;
}

export interface ChatLine {
  id: string;
  role: 'user' | 'assistant' | 'notice' | 'error' | 'steps' | 'change';
  text: string;
  /** The question this line answers: citations count within it. */
  run: number;
  /** Where the person goes on, in Lots. */
  link?: { href: string; label: string };
  steps?: readonly ToolStep[];
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
    limit: LISTED_SITES,
    pointLimit: MAPPED_SITES,
    siteTypes: strings(a['siteTypes']),
    lifecycles: strings(a['lifecycles']),
    serviceTypes: strings(a['serviceTypes']),
    equipment: objects(a['equipment']),
    siteAttributes: objects(a['siteAttributes']),
    place: typeof a['place'] === 'string' ? a['place'] : undefined,
    codePrefix: typeof a['codePrefix'] === 'string' ? a['codePrefix'] : undefined,
    region: typeof a['region'] === 'string' ? a['region'] : undefined,
    withinKm: typeof a['withinKm'] === 'number' ? a['withinKm'] : undefined,
    extent:
      Array.isArray(a['extent']) && a['extent'].every((n) => typeof n === 'number')
        ? a['extent']
        : undefined,
  };
}

const text = (v: unknown): string => (typeof v === 'string' ? v : '');

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
  private readonly markdown = inject(MARKDOWN_LEXER);
  private readonly http = inject(HttpClient);
  private readonly mapView = inject(MapView);
  private readonly tools = inject(Tools);
  private readonly router = inject(Router);
  private readonly selection = inject(Selection);
  private readonly hiddenFrame = inject(HIDDEN_FRAME);

  private readonly url = (this.config?.lotsUrl ?? '').replace(/\/+$/, '');
  private threadId: string = crypto.randomUUID();
  private agent: Agent | null = null;
  private seq = 0;
  private run = 0;
  /** The run in Lots, to stop it by. */
  private running: string | null = null;
  private stopped = false;
  /** What the person answered since the last question, so the agent knows how a change request ended. */
  private answers: string[] = [];
  /** A connection the context needs, as the stream said. */
  private needsConnection: { servers: string[]; url: unknown } | null = null;
  /** The question being answered or read. */
  private question = '';
  private restored = false;

  readonly context = this.config?.lotsContext ?? 'cmdb';
  readonly enabled =
    this.url.length > 0 && !!this.config?.lotsAuthority && !!this.config?.lotsClientId;
  readonly lines = signal<readonly ChatLine[]>([]);
  readonly busy = signal(false);
  readonly lexer = signal<Lexer | null>(null);
  /** A step the person asked to see, by following a citation: the chat scrolls to it. */
  readonly focused = signal<string | null>(null);
  /** The list of earlier chats while it is shown. */
  readonly history = signal<{ threads: readonly ChatThread[] | null; failed: boolean } | null>(
    null,
  );

  toggle(): void {
    this.tools.toggle('chat');
  }

  /** Starts over: a new thread in Lots, with nothing of this one. */
  newChat(): void {
    if (this.busy()) {
      return;
    }
    this.threadId = crypto.randomUUID();
    this.agent = null;
    this.answers = [];
    this.lines.set([]);
    this.history.set(null);
    this.remember(null);
  }

  /** Once signed in, the chat the person was in: a reload does not end a conversation. */
  async restore(): Promise<void> {
    if (this.restored || this.lines().length > 0) {
      return;
    }
    this.restored = true;
    const id = this.remembered();
    if (id) {
      await this.openThread(id).catch(() => this.remember(null));
    }
  }

  /** Shows the earlier chats, or goes back to this one. */
  async toggleHistory(): Promise<void> {
    if (this.history()) {
      this.history.set(null);
      return;
    }
    this.history.set({ threads: null, failed: false });
    try {
      const context = encodeURIComponent(this.context);
      const threads = await this.fromLots<ChatThread[]>(`/ag-ui/threads?context=${context}`);
      this.history.set({ threads, failed: false });
    } catch {
      this.history.set({ threads: [], failed: true });
    }
  }

  /** Opens an earlier chat to read and go on with. Nothing in it acts again: the map stays until the person asks. */
  async openThread(id: string): Promise<void> {
    if (this.busy()) {
      return;
    }
    const thread = await this.fromLots<{ id: string; turns: ThreadTurn[] }>(
      `/ag-ui/threads/${encodeURIComponent(id)}`,
    );
    this.threadId = thread.id;
    this.agent = null;
    this.answers = [];
    this.history.set(null);
    this.lines.set([]);
    this.remember(thread.id);
    for (const turn of thread.turns) {
      this.run++;
      this.question = turn.question;
      this.add({ role: 'user', text: turn.question });
      let sources = 0;
      for (const step of turn.steps) {
        const id = this.addStep(step.name);
        const content = step.result ?? '';
        const answered = answerOf(content) !== null;
        this.updateStep(id, {
          args: this.parsed(step.arguments),
          state: answered ? 'done' : 'failed',
          label: answered ? stepLabel(step.name, true) : stepLabel(step.name, false),
          source: answered ? ++sources : undefined,
          card: cardFor(step.name, content) ?? undefined,
          problem: answered ? undefined : content.slice(0, 400),
        });
        if (answered && step.name === REQUEST_CHANGE) {
          await this.askAboutChange(content, true);
        }
      }
      if (turn.answer) {
        this.add({ role: 'assistant', text: turn.answer });
      } else if (turn.status === 'Cancelled') {
        this.add({ role: 'notice', text: 'Stoppad.' });
      } else if (turn.status === 'Failed') {
        this.add({ role: 'error', text: 'Körningen i Lots misslyckades.' });
      }
    }
  }

  /** Marks a list from an earlier search on the map. */
  markOnMap(stepId: string): void {
    const step = this.step(stepId);
    if (step?.sites) {
      this.mapView.addLayer(
        stepId,
        step.question,
        step.sites.points,
        step.sites.extent,
        step.sites.total,
      );
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
    this.stopped = false;
    this.run++;
    this.question = text;
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
      await this.loadMarkdown();
      const agent = (this.agent ??= await this.factory(`${this.url}/ag-ui`, this.threadId));
      agent.headers = { Authorization: `Bearer ${token}` };
      const context = this.screen();
      this.answers = [];
      this.remember(this.threadId);
      const send = async () => {
        this.needsConnection = null;
        // Lots holds the earlier turns of the thread itself and reads only this one.
        agent.setMessages([{ id: `user-${++this.seq}`, role: 'user', content: text }]);
        await agent.runAgent(
          { forwardedProps: { context: this.context }, context },
          this.subscriber(),
        );
      };
      await send();
      const needed = this.connectionNeeded();
      if (needed) {
        const close = await this.connectAccount(needed.servers);
        for (const ms of CONNECT_WAITS_MS) {
          if (!close || this.stopped) {
            break;
          }
          await wait(ms);
          await send();
          if (!this.connectionNeeded()) {
            break;
          }
        }
        close?.();
        // It could not be done without the person: the link in Lots is there for that.
        if (this.connectionNeeded() && !this.stopped) {
          this.add({
            role: 'notice',
            text: 'Kontexten behöver ditt eget konto, så att svaren följer din behörighet. Koppla kontot i Lots en gång och fråga sedan igen.',
            link: this.link(needed.url, 'Koppla kontot i Lots'),
          });
        }
      }
    } catch (e: unknown) {
      if (!this.stopped) {
        this.add({ role: 'error', text: this.problem(e) });
      }
    } finally {
      this.settle();
      this.running = null;
      this.busy.set(false);
    }
  }

  private connectionNeeded(): { servers: string[]; url: unknown } | null {
    return this.needsConnection;
  }

  private async loadMarkdown(): Promise<void> {
    if (!this.lexer()) {
      this.lexer.set(await this.markdown());
    }
  }

  /** A call to Lots, with the Lots token. Not through HttpClient: cmdb's own token is added there, and it must not leave cmdb. */
  private async fromLots<T>(path: string): Promise<T> {
    const token = await this.lots.token();
    if (!token) {
      throw new Error('Not signed in to Lots.');
    }
    const response = await fetch(`${this.url}${path}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    if (!response.ok) {
      throw new Error(`Lots answered ${response.status}.`);
    }
    await this.loadMarkdown();
    return (await response.json()) as T;
  }

  /**
   * Connects the person's account at what the context needs, in the session they already have, so the first question just works.
   * Only at the identity provider we sign in at. Returns how to close the frame, or null when it could not start.
   */
  private async connectAccount(servers: readonly string[]): Promise<(() => void) | null> {
    const provider = new URL(this.config?.lotsAuthority ?? this.url).origin;
    const closers: (() => void)[] = [];
    for (const server of servers) {
      try {
        const token = await this.lots.token();
        const response = await fetch(
          `${this.url}/integrations/connections/${encodeURIComponent(server)}`,
          { method: 'POST', headers: { Authorization: `Bearer ${token}` } },
        );
        const { authorizeUrl } = (await response.json()) as { authorizeUrl?: string };
        const target = authorizeUrl ? new URL(authorizeUrl) : null;
        if (target?.origin === provider) {
          target.searchParams.set('prompt', 'none');
          closers.push(this.hiddenFrame(target.href));
        }
      } catch {
        // Left to the link in Lots.
      }
    }
    return closers.length > 0 ? () => closers.forEach((close) => close()) : null;
  }

  private storageKey(): string {
    return `cmdb.chat.thread.${this.lots.subject() ?? ''}`;
  }

  /** Which chat the person is in. A convenience: without it a reload just starts a new one. */
  private remember(id: string | null): void {
    try {
      if (id) {
        localStorage.setItem(this.storageKey(), id);
      } else {
        localStorage.removeItem(this.storageKey());
      }
    } catch {
      // Private windows and blocked storage.
    }
  }

  private remembered(): string | null {
    try {
      return localStorage.getItem(this.storageKey());
    } catch {
      return null;
    }
  }

  private parsed(json: string | null | undefined): unknown {
    try {
      return json ? JSON.parse(json) : undefined;
    } catch {
      return undefined;
    }
  }

  /** Stops the answer, and the run in Lots: a run outlives its stream, so closing the stream alone would not. */
  async stop(): Promise<void> {
    if (!this.busy() || this.stopped) {
      return;
    }
    this.stopped = true;
    const run = this.running;
    this.agent?.abortRun();
    this.add({ role: 'notice', text: 'Stoppad.' });
    if (run) {
      const token = await this.lots.token();
      // Not through HttpClient: cmdb's own token is added there, and it must not leave cmdb.
      await fetch(`${this.url}/runs/${encodeURIComponent(run)}/cancel`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}` },
      }).catch(() => undefined);
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

  /** Opens an object's panel, and takes the map there when its position is known. */
  openObject(ref: string, at?: { x: number; y: number }): void {
    if (!OBJECT_REF.test(ref)) {
      return;
    }
    void this.router.navigate(['/'], {
      queryParams: { p: ref },
      queryParamsHandling: 'merge',
    });
    if (at) {
      this.mapView.focus(at);
    }
  }

  /** Opens or closes a step. A search the agent made shows every match when opened, from our own API. */
  toggleStep(id: string): void {
    const step = this.step(id);
    if (!step) {
      return;
    }
    this.updateStep(id, { open: !step.open });
    const lists = step.tool === FIND_SITES || step.tool === SHOW_ON_MAP;
    if (!step.open && lists && !step.sites && step.state === 'done') {
      void this.listSites(id, step.args);
    }
  }

  /** The step a citation points at, in the run of the answer it stands in. */
  source(run: number, source: number): ToolStep | null {
    for (const line of this.lines()) {
      if (line.run === run) {
        const step = line.steps?.find((s) => s.source === source);
        if (step) {
          return step;
        }
      }
    }
    return null;
  }

  /** Follows a citation: opens the step it comes from and brings it into view. */
  showSource(run: number, source: number): void {
    const step = this.source(run, source);
    if (step) {
      if (!step.open) {
        this.toggleStep(step.id);
      }
      this.focused.set(step.id);
    }
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
    const layers = this.mapView.layers().filter((l) => l.visible);
    if (layers.length > 0) {
      items.push({
        description: 'Sets of sites marked on the map, the top one last',
        value: layers.map((l) => `${l.label} (${l.total})`).join('; '),
      });
    }
    const selected = this.selection.current();
    if (selected) {
      const shown = selected.siteIds.slice(0, 20).map((id) => `site:${id}`);
      const rest = selected.siteIds.length - shown.length;
      items.push({
        description: 'Selected on the map',
        value: `${shown.join(', ')}${rest > 0 ? ` and ${rest} more` : ''}`,
      });
    }
    for (const answer of this.answers) {
      items.push({ description: 'Since the last question', value: answer });
    }
    return items;
  }

  private subscriber(): AgentSubscriber {
    const lineOf = new Map<string, string>();
    const stepOf = new Map<string, string>();
    let sources = 0;
    return {
      onRunStartedEvent: ({ event }) => {
        this.running = event.runId;
      },
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
      onToolCallStartEvent: ({ event }) => {
        stepOf.set(event.toolCallId, this.addStep(event.toolCallName));
      },
      onToolCallEndEvent: ({ event, toolCallArgs }) => {
        const id = stepOf.get(event.toolCallId);
        if (id) {
          this.updateStep(id, { args: toolCallArgs });
        }
      },
      onToolCallResultEvent: ({ event }) => {
        const id = stepOf.get(event.toolCallId);
        const step = id ? this.step(id) : null;
        if (!id || !step) {
          return;
        }
        // A refused or failed call has no result to show, and Lots numbers only those that gave one.
        if (!answerOf(event.content)) {
          this.updateStep(id, { state: 'failed', problem: String(event.content).slice(0, 400) });
          return;
        }
        this.updateStep(id, {
          state: 'done',
          label: stepLabel(step.tool, true),
          source: ++sources,
          card: cardFor(step.tool, event.content) ?? undefined,
        });
        if (step.tool === SHOW_ON_MAP) {
          void this.show(id, step.args);
        } else if (step.tool === OPEN_OBJECT) {
          this.openObject(this.field(event.content, 'ref'));
        } else if (step.tool === REQUEST_CHANGE) {
          void this.askAboutChange(event.content);
        }
      },
      onCustomEvent: ({ event }) => this.custom(event.name, event.value),
      onRunErrorEvent: ({ event }) => {
        // Stopping ends the stream with an error of its own, which is no news to the one who stopped it.
        if (!this.stopped) {
          this.add({ role: 'error', text: event.message || 'Körningen i Lots misslyckades.' });
        }
      },
    };
  }

  /** Whatever still runs when the stream ends has no answer coming. */
  private settle(): void {
    this.lines.update((all) =>
      all.map((l) =>
        l.steps?.some((s) => s.state === 'running')
          ? {
              ...l,
              steps: l.steps.map((s) =>
                s.state === 'running' ? { ...s, state: 'failed' as const } : s,
              ),
            }
          : l,
      ),
    );
  }

  private step(id: string): ToolStep | null {
    for (const line of this.lines()) {
      const step = line.steps?.find((s) => s.id === id);
      if (step) {
        return step;
      }
    }
    return null;
  }

  private updateStep(id: string, next: Partial<ToolStep>): void {
    this.lines.update((all) =>
      all.map((l) =>
        l.steps?.some((s) => s.id === id)
          ? { ...l, steps: l.steps.map((s) => (s.id === id ? { ...s, ...next } : s)) }
          : l,
      ),
    );
  }

  /** Steps in a row share a line. */
  private addStep(tool: string): string {
    const id = `step-${++this.seq}`;
    const step: ToolStep = {
      id,
      tool,
      label: stepLabel(tool, false),
      state: 'running',
      question: this.question,
      open: false,
    };
    const last = this.lines().at(-1);
    if (last?.role === 'steps' && last.run === this.run) {
      this.lines.update((all) =>
        all.map((l) => (l.id === last.id ? { ...l, steps: [...(l.steps ?? []), step] } : l)),
      );
    } else {
      this.add({ role: 'steps', text: '', steps: [step] });
    }
    return id;
  }

  /**
   * Reads the plan from cmdb and asks. Only a draft of the person's own comes this far, whatever the call claimed. From the history
   * it shows how the question ended, and one that was never answered can still be.
   */
  private async askAboutChange(content: unknown, earlier = false): Promise<void> {
    const plan = text((answerOf(content) as { plan?: { ref?: unknown } } | null)?.plan?.ref);
    const id = PLAN_REF.exec(plan)?.[1];
    if (!id) {
      return;
    }
    try {
      const [detail, me] = await Promise.all([
        firstValueFrom(this.http.get<PlanDetail>(`/api/plans/${id}`)),
        firstValueFrom(this.http.get<{ username: string }>('/api/me')),
      ]);
      if (detail.plan.createdBy !== me.username || (!earlier && detail.plan.status !== 'draft')) {
        if (!earlier) {
          this.add({
            role: 'error',
            text: 'Ändringsbegäran går inte att besvara här: planen är inte ett utkast av dig.',
          });
        }
        return;
      }
      this.add({
        role: 'change',
        text: '',
        change: {
          planId: detail.plan.id,
          changes: detail.operations.map((o) => o.summary),
          problems: detail.operations.flatMap((o) => (o.problem ? [o.problem] : [])),
          state:
            detail.plan.status === 'applied'
              ? 'written'
              : detail.plan.status === 'cancelled'
                ? 'declined'
                : 'asking',
        },
      });
    } catch {
      if (!earlier) {
        this.add({ role: 'error', text: 'Kunde inte läsa ändringsbegäran från cmdb.' });
      }
    }
  }

  private field(content: unknown, key: string): string {
    return text((answerOf(content) as Record<string, unknown> | null)?.[key]);
  }

  /** Marks what the question gives the person, from our own API under their own scope, and brings the map into view. */
  private async show(stepId: string, args: unknown): Promise<void> {
    this.updateStep(stepId, { open: true, loading: true });
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
      // Nothing to show is no layer to keep track of.
      if (result.points.length > 0) {
        this.mapView.addLayer(
          stepId,
          this.step(stepId)?.question ?? '',
          result.points,
          result.extent,
          result.total,
        );
      }
      this.updateStep(stepId, {
        loading: false,
        sites: {
          rows: result.sites,
          total: result.total,
          points: result.points,
          extent: result.extent,
          positionsHidden: result.total > 0 && result.points.length === 0,
        },
      });
    } catch (e: unknown) {
      this.failedList(stepId, e, 'Kunde inte markera siterna på kartan.');
    }
  }

  private async listSites(stepId: string, args: unknown): Promise<void> {
    this.updateStep(stepId, { loading: true });
    try {
      const result = await firstValueFrom(
        this.http.post<SiteQueryResult>('/api/query/sites', siteQueryFrom(args)),
      );
      this.updateStep(stepId, {
        loading: false,
        sites: {
          rows: result.sites,
          total: result.total,
          points: result.points,
          extent: result.extent,
          positionsHidden: result.total > 0 && result.points.length === 0,
        },
      });
    } catch (e: unknown) {
      this.failedList(stepId, e, 'Kunde inte hämta siterna.');
    }
  }

  private failedList(stepId: string, e: unknown, fallback: string): void {
    const errors = (e as { error?: { errors?: Record<string, string[]> } }).error?.errors;
    this.updateStep(stepId, {
      loading: false,
      problem: errors ? Object.values(errors).flat().join(' ') : fallback,
    });
  }

  private custom(name: string, value: unknown): void {
    const v = (value ?? {}) as { url?: unknown; servers?: unknown };
    const link = (label: string) => this.link(v.url, label);
    if (name === 'lots.connect') {
      this.needsConnection = {
        servers: Array.isArray(v.servers)
          ? v.servers.filter((s): s is string => typeof s === 'string')
          : [],
        url: v.url,
      };
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

  private add(line: Omit<ChatLine, 'id' | 'run'>): string {
    const id = `line-${++this.seq}`;
    this.lines.update((all) => [...all, { ...line, id, run: this.run }]);
    return id;
  }
}
