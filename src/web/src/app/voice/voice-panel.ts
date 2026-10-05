import { HttpClient, httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { MapView, Operations } from '../map/map-view';
import { PanelStack } from '../shell/panels';
import { Tools } from '../shell/tools';
import { VoiceRoiComponent } from './roi';
import { VoiceCall } from './voice-call';

/** GET /api/incidents (#135) */
export interface Incident {
  number: string;
  resolvedAt?: string | null;
  resolvedBy?: string | null;
  priority: 'P1' | 'P2' | 'P3';
  status: string;
  siteId: number;
  siteCode: string;
  siteName: string;
  reference: string;
  description: string;
  observations: string;
  reportedBy: string;
  conversationId: string;
  createdAt: string;
  enrichment: {
    affected?: number;
    down?: number;
    criticalDown?: number;
    falseRedundancy?: number;
    summary?: string;
  };
}

/** GET /api/risks (#137) */
export interface Risk {
  id: string;
  kind: 'digging' | 'false-redundancy' | 'battery' | 'classification';
  title: string;
  description: string;
  reference: string;
  siteId: number;
  siteCode: string;
  siteName: string;
  affectedServices: number;
  criticalServices: number;
  responsibleEmployeeId: string;
  responsibleName: string;
  suggestedAction: string;
}

export const riskKindLabels: Record<Risk['kind'], string> = {
  digging: 'Grävning',
  'false-redundancy': 'Falsk redundans',
  battery: 'Reservkraft',
  classification: 'Klassningskrav',
};

/** GET /api/voice/service-requests (ADR-0016) */
export interface ServiceRequests {
  waiting: number;
  queueMinutes: number;
  requests: {
    number: string;
    kind: 'tag' | 'password' | 'equipment' | 'callback';
    status: string;
    employeeId: string | null;
    callerName: string;
    summary: string;
    conversationId: string;
    createdAt: string;
  }[];
}

export const requestKindLabels: Record<ServiceRequests['requests'][number]['kind'], string> = {
  tag: 'Passertagg',
  password: 'Lösenord',
  equipment: 'Utrustning',
  callback: 'Uppringning',
};

/** GET /api/voice/activity (ADR-0015) */
export interface VoiceActivity {
  sms: { toPhone: string; employeeId: string; body: string; createdAt: string }[];
  calls: {
    conversationId: string;
    employeeId: string | null;
    tool: string;
    outcome: string;
    milliseconds: number;
    createdAt: string;
  }[];
}

export const priorityLabels: Record<Incident['priority'], string> = {
  P1: 'P1 · jour larmad',
  P2: 'P2',
  P3: 'P3',
};

const POLL_MS = 3000;
const SHOWN_INCIDENTS = 5;
const FRESH_SMS_MS = 5 * 60_000;

/**
 * The operations agent seen from the NOC (#130): risks and incidents, and for the demo the stubbed SMS outbox (the
 * one-time codes) and the tool calls per call. Tabs and folded rows keep it short as calls pile up (#148). Polls while
 * open.
 */
@Component({
  selector: 'cmdb-voice-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [VoiceRoiComponent],
  template: `
    <header class="head">
      <h2>Driftagent</h2>
      <button type="button" class="close" aria-label="Stäng driftagenten" (click)="tools.close()">
        ×
      </button>
    </header>
    <div class="bar">
      @if (call.enabled) {
        <button type="button" class="primary" (click)="callAgent()">Ring service desk</button>
      }
      <div class="tabs" role="tablist" aria-label="Driftagent">
        @for (t of tabs; track t.key) {
          <button
            type="button"
            role="tab"
            [attr.aria-selected]="tab() === t.key"
            (click)="tab.set(t.key)"
          >
            {{ t.label }}
          </button>
        }
      </div>
    </div>
    @if (latestSms(); as s) {
      <p class="sms latest" aria-live="polite">
        <span class="muted">Senaste SMS {{ time(s.createdAt) }} till anst. {{ s.employeeId }}</span
        ><br />
        {{ s.body }}
      </p>
    }
    <div class="body">
      @switch (tab()) {
        @case ('overview') {
          <section aria-labelledby="risks">
            <h3 id="risks">
              Risker <span class="count">{{ risks().length }}</span>
            </h3>
            @for (r of risks(); track r.id) {
              <details class="row">
                <summary>
                  <span class="status" [attr.data-kind]="r.kind">
                    <span class="dot" aria-hidden="true"></span>{{ riskKindLabels[r.kind] }}
                  </span>
                  <span class="grow">{{ r.siteName }}</span>
                  <span class="muted"
                    >{{ r.criticalServices }} kritiska av {{ r.affectedServices }} tjänster</span
                  >
                </summary>
                <p class="title">{{ r.title }}</p>
                <p>{{ r.description }}</p>
                <p class="muted">Åtgärd: {{ r.suggestedAction }}</p>
                <p class="actions">
                  <button type="button" class="link" (click)="openSite(r.siteId)">
                    {{ r.siteName }}
                  </button>
                  @if (call.enabled) {
                    <button type="button" class="secondary" (click)="callResponsible(r)">
                      Ring {{ r.responsibleName }}
                    </button>
                  }
                </p>
              </details>
            } @empty {
              <p class="muted">Inga risker.</p>
            }
          </section>

          <section aria-labelledby="incidents">
            <h3 id="incidents" class="with-action">
              <span
                >Ärenden <span class="count">{{ openIncidents().length }}</span></span
              >
              @if (openIncidents().length > 1) {
                <button type="button" class="link" (click)="resolveAll()">Lös alla</button>
              }
            </h3>
            @if (actionError(); as e) {
              <p class="error" role="alert">{{ e }}</p>
            }
            @for (i of shownIncidents(); track i.number) {
              <details class="row" [class.closed]="i.status !== 'open'">
                <summary>
                  <span class="status" [attr.data-priority]="i.priority">
                    <span class="dot" aria-hidden="true"></span>{{ priorityLabels[i.priority] }}
                  </span>
                  <span>{{ i.number }}</span>
                  <span class="grow">{{ i.siteName }}</span>
                  @if (i.status === 'open') {
                    <button
                      type="button"
                      class="icon"
                      [attr.aria-pressed]="onMap().has(i.number)"
                      [attr.aria-label]="
                        (onMap().has(i.number) ? 'Dölj ' : 'Visa ') + i.number + ' på kartan'
                      "
                      [title]="
                        onMap().has(i.number)
                          ? 'Dölj påverkan på kartan'
                          : 'Visa påverkan på kartan'
                      "
                      (click)="toggleOnMap(i.number, $event)"
                    >
                      ◉
                    </button>
                    <button
                      type="button"
                      class="secondary small"
                      (click)="resolve(i.number, $event)"
                    >
                      Lös
                    </button>
                  } @else {
                    <span class="muted">Löst</span>
                  }
                  <time class="muted" [attr.datetime]="i.createdAt">{{ time(i.createdAt) }}</time>
                </summary>
                <button type="button" class="link" (click)="openSite(i.siteId)">
                  {{ i.siteName }} ({{ i.siteCode }})
                </button>
                <p>{{ i.description }}</p>
                @if (i.enrichment.summary) {
                  <p class="summary">{{ i.enrichment.summary }}</p>
                }
                @if (i.observations) {
                  <p class="muted">Observationer: {{ i.observations }}</p>
                }
                <p class="muted">
                  Anmält av {{ i.reportedBy }} · samtal {{ short(i.conversationId) }}
                </p>
                @if (i.resolvedAt) {
                  <p class="muted">Löst {{ time(i.resolvedAt) }} av {{ i.resolvedBy }}</p>
                }
              </details>
            } @empty {
              <p class="muted">Inga ärenden ännu.</p>
            }
            <p class="more">
              @if (openIncidents().length > shownIncidents().length && !allIncidents()) {
                <button type="button" class="link" (click)="allIncidents.set(true)">
                  Visa alla {{ openIncidents().length }}
                </button>
              }
              @if (resolvedCount()) {
                <button type="button" class="link" (click)="showResolved.update((v) => !v)">
                  {{ showResolved() ? 'Dölj lösta' : 'Visa lösta (' + resolvedCount() + ')' }}
                </button>
              }
            </p>
          </section>
        }
        @case ('calls') {
          @if (serviceRequests(); as sr) {
            <section aria-labelledby="requests">
              <h3 id="requests">
                Service desk <span class="count">{{ sr.requests.length }}</span>
              </h3>
              <p class="muted">
                Uppringningskö: {{ sr.waiting }} väntar, cirka {{ sr.queueMinutes }} minuter.
              </p>
              @for (q of sr.requests.slice(0, 8); track q.number) {
                <details class="row">
                  <summary>
                    <span class="status" [attr.data-kind]="q.kind">
                      <span class="dot" aria-hidden="true"></span>{{ requestKindLabels[q.kind] }}
                    </span>
                    <span>{{ q.number }}</span>
                    <span class="grow">{{ q.callerName }}</span>
                    @if (q.status === 'open') {
                      <button
                        type="button"
                        class="secondary small"
                        (click)="complete(q.number, $event)"
                      >
                        Klar
                      </button>
                    } @else {
                      <span class="muted">Klar</span>
                    }
                    <time class="muted" [attr.datetime]="q.createdAt">{{ time(q.createdAt) }}</time>
                  </summary>
                  <p>{{ q.summary }}</p>
                  <p class="muted">
                    {{ q.employeeId ? 'anst. ' + q.employeeId : 'overifierad' }} · samtal
                    {{ short(q.conversationId) }}
                  </p>
                </details>
              } @empty {
                <p class="muted">Inga serviceärenden.</p>
              }
            </section>
          }
          @if (activity(); as a) {
            <section aria-labelledby="calls">
              <h3 id="calls">
                Samtal <span class="count">{{ conversations().length }}</span>
              </h3>
              @for (c of conversations(); track c.id; let first = $first) {
                <details class="row" [open]="first">
                  <summary>
                    <time class="muted" [attr.datetime]="c.start">{{ time(c.start) }}</time>
                    <span class="grow">{{
                      c.employeeId ? 'anst. ' + c.employeeId : 'overifierad'
                    }}</span>
                    <span class="muted">{{ c.calls.length }} anrop</span>
                    @if (c.refused) {
                      <span class="status" data-priority="P2">
                        <span class="dot" aria-hidden="true"></span>{{ c.refused }} nekade
                      </span>
                    }
                  </summary>
                  <table>
                    <tbody>
                      @for (t of c.calls; track $index) {
                        <tr>
                          <td>{{ time(t.createdAt) }}</td>
                          <td>{{ t.tool }}</td>
                          <td>{{ t.outcome }}</td>
                          <td class="num">{{ t.milliseconds }} ms</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </details>
              } @empty {
                <p class="muted">Inga samtal ännu.</p>
              }
            </section>
            <details>
              <summary>SMS-utkorg <span class="muted">(stubbad, bara för demon)</span></summary>
              @for (s of a.sms; track s.createdAt + s.toPhone) {
                <p class="sms">
                  <time [attr.datetime]="s.createdAt">{{ time(s.createdAt) }}</time>
                  <span class="muted">till {{ s.toPhone }} (anst. {{ s.employeeId }})</span><br />
                  {{ s.body }}
                </p>
              } @empty {
                <p class="muted">Inga SMS.</p>
              }
            </details>
          } @else if (activityForbidden()) {
            <p class="muted">
              SMS-utkorgen och verktygsanropen visas bara med behörighet till hela nätet.
            </p>
          }
        }
        @case ('roi') {
          <cmdb-voice-roi />
        }
      }
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      min-height: 36px;
      padding: 0 var(--space-2) 0 var(--space-4);
      border-bottom: var(--line);
      h2 {
        margin: 0;
        font-size: var(--text-md);
        font-weight: 600;
      }
    }
    .close {
      width: 26px;
      height: 26px;
      border: 0;
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    .bar {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: center;
      justify-content: space-between;
      padding: var(--space-2) var(--space-4);
      border-bottom: var(--line);
    }
    .tabs {
      display: flex;
      gap: var(--space-1);
    }
    .tabs button {
      border: var(--line);
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text-muted);
      font: inherit;
      font-size: var(--text-sm);
      padding: 2px var(--space-2);
      cursor: pointer;
      &[aria-selected='true'] {
        color: var(--text);
        background: var(--surface-2);
      }
    }
    .latest {
      margin: var(--space-2) var(--space-4) 0;
    }
    .count {
      color: var(--text-muted);
      font-weight: 400;
      font-size: var(--text-sm);
    }
    .row {
      border-bottom: var(--line);
      padding: var(--space-1) 0;
      > summary {
        display: flex;
        gap: var(--space-2);
        align-items: center;
        margin: 0;
        font-weight: 400;
        font-size: var(--text-sm);
        list-style: none;
        &::-webkit-details-marker {
          display: none;
        }
      }
      &[open] > summary {
        margin-bottom: var(--space-1);
      }
    }
    .grow {
      flex: 1;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .more {
      display: flex;
      gap: var(--space-3);
      margin-top: var(--space-2);
    }
    .with-action {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
    }
    .closed {
      opacity: 0.6;
    }
    .error {
      color: var(--status-conflict);
      font-size: var(--text-sm);
    }
    .small {
      padding: 0 var(--space-2);
      font-size: var(--text-xs);
    }
    .icon {
      padding: 0 var(--space-1);
      border: 0;
      background: none;
      color: var(--text-faint);
      font-size: var(--text-md);
      line-height: 1;
      cursor: pointer;
      &[aria-pressed='true'] {
        color: var(--status-conflict);
      }
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: var(--space-3) var(--space-4);
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }
    h3 {
      margin: 0 0 var(--space-2);
      font-size: var(--text-md);
    }
    p {
      margin: 0 0 var(--space-1);
      font-size: var(--text-sm);
    }
    .incident {
      padding: var(--space-2) 0;
      border-bottom: var(--line);
    }
    .meta {
      display: flex;
      gap: var(--space-2);
      align-items: center;
      font-size: var(--text-xs);
      color: var(--text-muted);
    }
    .status {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
      font-weight: 600;
      color: var(--text);
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--status-in-service);
    }
    [data-priority='P1'] .dot {
      background: var(--status-conflict);
    }
    [data-priority='P2'] .dot {
      background: var(--status-decommissioning);
    }
    .summary {
      color: var(--text);
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .link {
      padding: 0;
      border: 0;
      background: none;
      color: var(--action);
      font: inherit;
      font-size: var(--text-sm);
      font-weight: 600;
      cursor: pointer;
    }
    summary {
      font-weight: 600;
      cursor: pointer;
      margin-bottom: var(--space-2);
    }
    .risk {
      padding: var(--space-2) 0;
      border-bottom: var(--line);
    }
    .title {
      font-weight: 600;
    }
    .actions {
      display: flex;
      gap: var(--space-2);
      align-items: center;
    }
    [data-kind='digging'] .dot,
    [data-kind='false-redundancy'] .dot,
    [data-kind='classification'] .dot {
      background: var(--status-conflict);
    }
    [data-kind='battery'] .dot {
      background: var(--status-decommissioning);
    }
    .primary,
    .secondary {
      padding: var(--space-1) var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
    }
    .primary {
      background: var(--action);
      color: var(--on-action, #fff);
      border-color: var(--action);
    }
    .secondary {
      background: var(--surface-2);
      color: var(--text);
    }
    .sms {
      padding: var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-2);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-xs);
    }
    th,
    td {
      padding: 2px var(--space-1);
      text-align: left;
      border-bottom: var(--line);
    }
    .num {
      text-align: right;
    }
  `,
})
export class VoicePanelComponent {
  protected readonly tools = inject(Tools);
  protected readonly call = inject(VoiceCall);
  protected readonly riskKindLabels = riskKindLabels;
  protected readonly requestKindLabels = requestKindLabels;
  private readonly panels = inject(PanelStack);
  protected readonly priorityLabels = priorityLabels;

  protected readonly tabs = [
    { key: 'overview', label: 'Översikt' },
    { key: 'calls', label: 'Samtal' },
    { key: 'roi', label: 'ROI' },
  ] as const;
  protected readonly tab = signal<'overview' | 'calls' | 'roi'>('overview');
  protected readonly allIncidents = signal(false);

  private readonly tick = signal(0);

  private readonly incidentsResource = httpResource<Incident[]>(() => ({
    url: '/api/incidents',
    params: { t: this.tick() },
  }));
  private readonly risksResource = httpResource<Risk[]>(() => ({
    url: '/api/risks',
    // Risks change slowly: every tenth poll.
    params: { t: Math.floor(this.tick() / 10) },
  }));
  private lastRisks: Risk[] = [];
  protected readonly risks = computed(() => {
    const value = this.risksResource.hasValue() ? this.risksResource.value() : undefined;
    if (value) {
      this.lastRisks = value;
    }
    return this.lastRisks;
  });

  private readonly requestsResource = httpResource<ServiceRequests>(() => ({
    url: '/api/voice/service-requests',
    params: { t: this.tick() },
  }));
  private lastRequests: ServiceRequests | undefined;
  protected readonly serviceRequests = computed(() => {
    const value = this.requestsResource.hasValue() ? this.requestsResource.value() : undefined;
    if (value) {
      this.lastRequests = value;
    }
    return this.lastRequests;
  });

  private readonly activityResource = httpResource<VoiceActivity>(() => ({
    url: '/api/voice/activity',
    params: { t: this.tick() },
  }));

  // Keep the last answer while the next poll is in flight, so the lists do not flicker.
  private lastIncidents: Incident[] = [];
  private lastActivity: VoiceActivity | undefined;

  protected readonly incidents = computed(() => {
    const value = this.incidentsResource.hasValue() ? this.incidentsResource.value() : undefined;
    if (value) {
      this.lastIncidents = value;
    }
    return this.lastIncidents;
  });
  protected readonly activity = computed(() => {
    const value = this.activityResource.hasValue() ? this.activityResource.value() : undefined;
    if (value) {
      this.lastActivity = value;
    }
    return this.lastActivity;
  });
  protected readonly showResolved = signal(false);
  protected readonly openIncidents = computed(() =>
    this.incidents().filter((i) => i.status === 'open'),
  );
  protected readonly resolvedCount = computed(
    () => this.incidents().length - this.openIncidents().length,
  );
  protected readonly shownIncidents = computed(() => {
    const list = this.showResolved() ? this.incidents() : this.openIncidents();
    return this.allIncidents() ? list : list.slice(0, SHOWN_INCIDENTS);
  });
  protected readonly actionError = signal<string | null>(null);

  /** Incidents whose impact the map shows (#161), so overlapping ones can be told apart. */
  protected readonly onMap = signal<ReadonlySet<string>>(new Set());
  private readonly impacts = signal<ReadonlyMap<string, NonNullable<Operations['live']>['impact']>>(
    new Map(),
  );
  private readonly http = inject(HttpClient);

  protected toggleOnMap(number: string, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    const shown = new Set(this.onMap());
    if (shown.delete(number)) {
      this.onMap.set(shown);
      return;
    }
    this.showOnMap(number);
  }

  private showOnMap(number: string): void {
    this.onMap.set(new Set(this.onMap()).add(number));
    this.http
      .get<NonNullable<NonNullable<Operations['live']>['impact']>>(
        `/api/incidents/${number}/impact`,
      )
      .subscribe({
        next: (impact) => this.impacts.update((m) => new Map(m).set(number, impact)),
        error: () => this.actionError.set(`Påverkan för ${number} kunde inte hämtas.`),
      });
  }

  protected resolve(number: string, event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    this.actionError.set(null);
    this.http.post(`/api/incidents/${number}/resolve`, null).subscribe({
      next: () => {
        const shown = new Set(this.onMap());
        shown.delete(number);
        this.onMap.set(shown);
        this.tick.update((t) => t + 1);
      },
      error: (e: { status?: number }) =>
        this.actionError.set(
          e.status === 403
            ? 'Du behöver skrivbehörighet för att lösa ärenden.'
            : `${number} kunde inte lösas.`,
        ),
    });
  }

  protected resolveAll(): void {
    for (const i of this.openIncidents()) {
      this.resolve(i.number);
    }
  }

  protected complete(number: string, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.http.post(`/api/voice/service-requests/${number}/done`, null).subscribe({
      next: () => this.tick.update((t) => t + 1),
      error: () => this.actionError.set(`${number} kunde inte markeras som klar.`),
    });
  }

  /** The newest SMS while it is fresh: the code to read out during a call, whichever tab is open. */
  protected readonly latestSms = computed(() => {
    this.tick();
    const s = this.activity()?.sms[0];
    return s && Date.now() - new Date(s.createdAt).getTime() < FRESH_SMS_MS ? s : undefined;
  });

  /** Tool calls grouped per conversation, newest conversation first, calls in order. */
  protected readonly conversations = computed(() => {
    const groups = new Map<string, VoiceActivity['calls']>();
    for (const c of this.activity()?.calls ?? []) {
      groups.set(c.conversationId, [...(groups.get(c.conversationId) ?? []), c]);
    }
    return [...groups].map(([id, calls]) => {
      const ordered = [...calls].reverse();
      return {
        id,
        calls: ordered,
        start: ordered[0].createdAt,
        employeeId: ordered.find((c) => c.employeeId)?.employeeId ?? null,
        refused: ordered.filter((c) => c.outcome === 'refused').length,
      };
    });
  });

  protected readonly activityForbidden = computed(
    () => (this.activityResource.error() as { status?: number } | undefined)?.status === 403,
  );

  // The map's operations layer (#156) while the panel is open: incidents and the live call every poll, work areas
  // and risks every tenth.
  private readonly mapView = inject(MapView);
  private readonly liveResource = httpResource<Pick<Operations, 'incidents' | 'live'>>(() => ({
    url: '/api/operations/live',
    params: { t: this.tick() },
  }));
  private readonly worksResource = httpResource<Pick<Operations, 'works' | 'risks'>>(() => ({
    url: '/api/operations/works',
    params: { t: Math.floor(this.tick() / 10) },
  }));
  private lastLive: Pick<Operations, 'incidents' | 'live'> | undefined;
  /** Incidents whose eye was switched on because the live call created them (#163); each only once. */
  private readonly autoShown = new Set<string>();
  private lastWorks: Pick<Operations, 'works' | 'risks'> | undefined;

  constructor() {
    const timer = setInterval(() => this.tick.update((t) => t + 1), POLL_MS);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(timer);
      this.mapView.operations.set(null);
    });
    effect(() => {
      if (this.liveResource.hasValue()) {
        this.lastLive = this.liveResource.value();
        // The call's own incident takes over its impact, eye on, so it shows at once and can be hidden (#163).
        const live = this.lastLive.live;
        for (const i of this.lastLive.incidents) {
          if (
            live &&
            i.conversationId === live.conversationId &&
            i.reference === live.reference &&
            !this.autoShown.has(i.number)
          ) {
            this.autoShown.add(i.number);
            untracked(() => this.showOnMap(i.number));
          }
        }
      }
      if (this.worksResource.hasValue()) {
        this.lastWorks = this.worksResource.value();
      }
      if (this.lastLive || this.lastWorks) {
        const impacts = this.impacts();
        this.mapView.operations.set({
          incidents: this.lastLive?.incidents ?? [],
          live: this.lastLive?.live ?? null,
          works: this.lastWorks?.works ?? [],
          risks: this.lastWorks?.risks ?? [],
          incidentImpacts: [...this.onMap()]
            .filter((n) => impacts.get(n))
            .map((number) => ({ number, impact: impacts.get(number)! })),
        });
      }
    });
  }

  protected callAgent(): void {
    this.call.start({ variables: {}, label: 'Samtal med service desk' });
  }

  /** The proactive call (#137): the agent opens with the risk, and verifies the person before any detail. */
  protected callResponsible(risk: Risk): void {
    this.call.start({
      agent: 'noc',
      label: `Ringer ${risk.responsibleName}: ${riskKindLabels[risk.kind]}`,
      variables: {
        risk_id: risk.id,
        risk_title: risk.title,
        responsible_name: risk.responsibleName,
        responsible_employee_id: risk.responsibleEmployeeId,
      },
      firstMessage:
        `Hej ${risk.responsibleName}, det här är Sebastian på NOC. Jag ringer om en risk i nätet som du ansvarar för. ` +
        'Innan jag berättar mer behöver jag verifiera dig. Vad är ditt anställningsnummer?',
    });
  }

  protected openSite(id: number): void {
    this.panels.open({ type: 'site', id: String(id) });
  }

  protected time(iso: string): string {
    return new Date(iso).toLocaleTimeString('sv-SE', {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  }

  protected short(conversation: string): string {
    return conversation.length > 12 ? `…${conversation.slice(-8)}` : conversation;
  }
}
