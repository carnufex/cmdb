import { DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Auth } from '../auth/auth';
import { ActivePlan } from '../plans/active-plan';
import { Tools } from '../shell/tools';
import {
  objectTypeLabels,
  ReconciliationReport,
  ReconciliationSummary,
  reasonLabels,
  runState,
  runStateLabels,
  valueText,
} from './reconciliation-model';

/**
 * Reconciliation runs (#216, #217): what each source system differs in from cmdb, and the plans it made. The list
 * shows the caller's own runs, or every run for an unrestricted scope; a report opens the plans in the plan panel.
 */
@Component({
  selector: 'cmdb-reconciliation-panel',
  imports: [DecimalPipe],
  template: `
    <header class="head">
      @if (selected() !== null) {
        <button type="button" class="back" (click)="selected.set(null)">← Avstämningar</button>
      } @else {
        <h2>Avstämningar</h2>
      }
      <button type="button" class="close" aria-label="Stäng avstämningar" (click)="tools.close()">
        ×
      </button>
    </header>
    <div class="body">
      @if (selected() === null) {
        @if (runs.error()) {
          <p class="muted">Avstämningarna kunde inte läsas.</p>
        }
        <ul class="runs">
          @for (r of runs.value() ?? []; track r.id) {
            <li>
              <button type="button" (click)="selected.set(r.id)">
                <span class="title">{{ r.source }}</span>
                <span class="state" [attr.data-state]="state(r)"
                  ><span class="dot" aria-hidden="true"></span>{{ stateLabels[state(r)] }}</span
                >
                <span class="meta"
                  >{{ r.startedAt.slice(0, 16).replace('T', ' ') }} · {{ r.runBy }} ·
                  {{ r.elapsedMs / 1000 | number: '1.0-1' }} s</span
                >
              </button>
            </li>
          } @empty {
            @if (!runs.isLoading()) {
              <li class="muted">Inga avstämningar ännu. De körs med <code>cmdb sync</code>.</li>
            }
          }
        </ul>
      } @else if (report.value(); as r) {
        <h3>
          {{ r.source }}
          <span class="state" [attr.data-state]="reportState()"
            ><span class="dot" aria-hidden="true"></span>{{ stateLabels[reportState()] }}</span
          >
        </h3>
        <p class="meta">
          Avstämning {{ r.id }} · {{ r.elapsedMs / 1000 | number: '1.0-1' }} s ·
          {{ r.operations }} operationer
        </p>
        @if (r.reviewPlanId !== null || r.appliedPlanId !== null) {
          <p class="plans">
            @if (r.reviewPlanId !== null) {
              <button type="button" class="text-button" (click)="openPlan(r.reviewPlanId)">
                Plan för granskning
              </button>
            }
            @if (r.appliedPlanId !== null) {
              <button type="button" class="text-button" (click)="openPlan(r.appliedPlanId)">
                Införd plan
              </button>
            }
          </p>
        }
        @if (r.autoApplyProblem) {
          <p class="problem">Den betrodda planen fördes inte in: {{ r.autoApplyProblem }}</p>
        }
        @if (r.errors.length) {
          <h4>Fel i filerna</h4>
          <ul class="errors">
            @for (e of r.errors; track $index) {
              <li>{{ e }}</li>
            }
          </ul>
        }
        @if (r.counts.length) {
          <table>
            <caption class="visually-hidden">
              Per objekttyp
            </caption>
            <thead>
              <tr>
                <th scope="col">Typ</th>
                <th scope="col">Rapporterade</th>
                <th scope="col">Matchade</th>
                <th scope="col">Nya</th>
                <th scope="col">Ändrade</th>
                <th scope="col">Saknas</th>
                <th scope="col">Avvikelser</th>
                <th scope="col">Utanför</th>
              </tr>
            </thead>
            <tbody>
              @for (c of r.counts; track c.objectType) {
                <tr>
                  <th scope="row">{{ typeLabels[c.objectType] ?? c.objectType }}</th>
                  <td>{{ c.reported | number }}</td>
                  <td>{{ c.matched | number }}</td>
                  <td>{{ c.new | number }}</td>
                  <td>{{ c.changed | number }}</td>
                  <td>{{ c.missing | number }}</td>
                  <td>{{ c.deviations | number }}</td>
                  <td>{{ c.outsideScope | number }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
        @if (reasons().length) {
          <h4>Avvikelser</h4>
          <p class="reasons">
            @for (x of reasons(); track x.key) {
              <span>{{ x.label }}: {{ x.count | number }}</span>
            }
          </p>
          <ul class="deviations">
            @for (d of r.deviations.slice(0, 100); track $index) {
              <li>
                <span class="ref"
                  >{{ typeLabels[d.objectType] ?? d.objectType }} {{ d.externalId }}</span
                >
                <span class="attribute">{{ d.attribute }}</span>
                <span class="values"
                  >{{ value(d.source) }} i källan, {{ value(d.cmdb) }} i cmdb</span
                >
                <span class="reason">{{ reasonText(d.reason) }}</span>
              </li>
            }
          </ul>
          @if (r.deviations.length > 100) {
            <p class="muted">
              Visar 100 av {{ r.deviations.length | number }}. Hela rapporten finns i
              <code>cmdb reconciliation {{ r.id }}</code> och via MCP.
            </p>
          }
        }
        @for (n of r.notReconciled; track $index) {
          <p class="muted">{{ n }}</p>
        }
      } @else if (report.error()) {
        <p class="muted">Rapporten kunde inte läsas.</p>
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
    .back {
      border: 0;
      background: none;
      color: var(--action);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
      padding: 0;
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
      font-size: var(--text-sm);
    }
    .runs {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      button {
        width: 100%;
        display: grid;
        grid-template-columns: 1fr auto;
        gap: var(--space-1) var(--space-2);
        padding: var(--space-2);
        border: var(--line);
        border-radius: var(--radius-sm);
        background: none;
        color: inherit;
        font: inherit;
        text-align: left;
        cursor: pointer;
      }
      .meta {
        grid-column: 1 / -1;
      }
    }
    .title {
      font-weight: 600;
    }
    .state {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      color: var(--text-muted);
      font-size: var(--text-sm);
      font-weight: 400;
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--status-in-service);
    }
    [data-state='dry'] .dot {
      background: var(--status-planned);
    }
    [data-state='review'] .dot,
    [data-state='problem'] .dot {
      background: var(--status-decommissioning);
    }
    [data-state='failed'] .dot {
      background: var(--status-conflict);
    }
    h3 {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      margin: 0 0 var(--space-1);
      font-size: var(--text-md);
    }
    h4 {
      margin: var(--space-4) 0 var(--space-1);
      font-size: var(--text-sm);
    }
    .meta,
    .muted {
      color: var(--text-muted);
      font-size: var(--text-xs);
      margin: 0 0 var(--space-2);
    }
    .plans {
      display: flex;
      gap: var(--space-3);
    }
    .problem,
    .errors {
      color: var(--status-conflict);
    }
    table {
      width: 100%;
      margin-top: var(--space-3);
      border-collapse: collapse;
      font-size: var(--text-xs);
      font-variant-numeric: tabular-nums;
      th,
      td {
        padding: var(--space-1);
        border-bottom: var(--line);
        text-align: right;
      }
      th[scope='row'],
      thead th:first-child {
        text-align: left;
      }
    }
    .reasons {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1) var(--space-3);
      margin: 0 0 var(--space-2);
    }
    .deviations {
      list-style: none;
      margin: 0;
      padding: 0;
      li {
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 0 var(--space-2);
        padding: var(--space-1) 0;
        border-bottom: var(--line);
      }
      .ref {
        font-family: var(--font-mono);
        font-size: var(--text-xs);
      }
      .attribute {
        font-weight: 600;
      }
      .values,
      .reason {
        grid-column: 1 / -1;
        color: var(--text-muted);
        font-size: var(--text-xs);
      }
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReconciliationPanelComponent {
  private readonly auth = inject(Auth);
  private readonly plan = inject(ActivePlan);
  protected readonly tools = inject(Tools);

  protected readonly selected = signal<number | null>(null);
  protected readonly stateLabels = runStateLabels;
  protected readonly typeLabels = objectTypeLabels;
  protected readonly value = valueText;
  protected readonly state = runState;

  protected readonly runs = httpResource<ReconciliationSummary[]>(() =>
    this.auth.isAuthenticated() ? { url: '/api/reconciliations' } : undefined,
  );

  protected readonly report = httpResource<ReconciliationReport>(() => {
    const id = this.selected();
    return id === null ? undefined : { url: `/api/reconciliations/${id}` };
  });

  protected readonly reportState = computed(() => {
    const r = this.report.value();
    return r ? runState(r) : 'done';
  });

  protected readonly reasons = computed(() =>
    Object.entries(this.report.value()?.reasons ?? {})
      .map(([key, count]) => ({ key, count, label: reasonLabels[key] ?? key }))
      .sort((a, b) => b.count - a.count),
  );

  protected reasonText(reason: string): string {
    return reasonLabels[reason] ?? reason;
  }

  protected openPlan(id: number): void {
    this.plan.activate(id);
    this.tools.toggle('plans');
  }
}
