import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { PanelStack } from '../shell/panels';
import { Tools } from '../shell/tools';

/** GET /api/incidents (#135) */
export interface Incident {
  number: string;
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

/**
 * The operations agent seen from the NOC (#130): incidents it has created with their enrichment, and for the demo the
 * stubbed SMS outbox (the one-time codes) and every tool call on the voice channel. Polls while open.
 */
@Component({
  selector: 'cmdb-voice-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="head">
      <h2>Driftagent</h2>
      <button type="button" class="close" aria-label="Stäng driftagenten" (click)="tools.close()">
        ×
      </button>
    </header>
    <div class="body">
      <section aria-labelledby="incidents">
        <h3 id="incidents">Ärenden</h3>
        @for (i of incidents(); track i.number) {
          <article class="incident">
            <p class="meta">
              <span class="status" [attr.data-priority]="i.priority">
                <span class="dot" aria-hidden="true"></span>{{ priorityLabels[i.priority] }}
              </span>
              <span>{{ i.number }}</span>
              <time [attr.datetime]="i.createdAt">{{ time(i.createdAt) }}</time>
            </p>
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
            <p class="muted">Anmält av {{ i.reportedBy }} · samtal {{ short(i.conversationId) }}</p>
          </article>
        } @empty {
          <p class="muted">Inga ärenden ännu.</p>
        }
      </section>

      @if (activity(); as a) {
        <section aria-labelledby="sms">
          <h3 id="sms">SMS-utkorg <span class="muted">(stubbad, bara för demon)</span></h3>
          @for (s of a.sms; track s.createdAt + s.toPhone) {
            <p class="sms">
              <time [attr.datetime]="s.createdAt">{{ time(s.createdAt) }}</time>
              <span class="muted">till {{ s.toPhone }} (anst. {{ s.employeeId }})</span><br />
              {{ s.body }}
            </p>
          } @empty {
            <p class="muted">Inga SMS.</p>
          }
        </section>
        <section aria-labelledby="calls">
          <h3 id="calls">Verktygsanrop</h3>
          <table>
            <thead>
              <tr>
                <th scope="col">Tid</th>
                <th scope="col">Verktyg</th>
                <th scope="col">Utfall</th>
                <th scope="col">Uppringare</th>
                <th scope="col" class="num">ms</th>
              </tr>
            </thead>
            <tbody>
              @for (c of a.calls; track $index) {
                <tr>
                  <td>{{ time(c.createdAt) }}</td>
                  <td>{{ c.tool }}</td>
                  <td>{{ c.outcome }}</td>
                  <td>{{ c.employeeId ?? '–' }}</td>
                  <td class="num">{{ c.milliseconds }}</td>
                </tr>
              }
            </tbody>
          </table>
        </section>
      } @else if (activityForbidden()) {
        <p class="muted">
          SMS-utkorgen och verktygsanropen visas bara med behörighet till hela nätet.
        </p>
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
      background: var(--status-removed);
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
  private readonly panels = inject(PanelStack);
  protected readonly priorityLabels = priorityLabels;

  private readonly tick = signal(0);

  private readonly incidentsResource = httpResource<Incident[]>(() => ({
    url: '/api/incidents',
    params: { t: this.tick() },
  }));
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
  protected readonly activityForbidden = computed(
    () => (this.activityResource.error() as { status?: number } | undefined)?.status === 403,
  );

  constructor() {
    const timer = setInterval(() => this.tick.update((t) => t + 1), POLL_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
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
