import { HttpClient, HttpErrorResponse, httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ReservationView } from '../objects/claims';
import { PlanClassificationComponent } from './plan-classification';
import { PlanRouteComponent } from './plan-route';
import { PlanCreateComponent } from './plan-create';
import { PlanChangesComponent } from './plan-changes';
import { PlanDraft } from './plan-draft';
import { PlanImpactComponent } from './plan-impact';
import { PlanMaterialsComponent } from './plan-materials';
import { PlanListComponent } from './plan-list';
import { StatusComponent } from '../shell/status';
import { Tools } from '../shell/tools';
import { ActivePlan } from './active-plan';
import {
  ApplyResult,
  byPlan,
  ChangeCategory,
  changeCategories,
  changeCategoryLabels,
  changeCounts,
  nextStep,
  PlanDetail,
  PlanSummary,
  planLifecycle,
  planStatusLabels,
} from './plan-model';
import { ToolSizeComponent } from '../shell/tool-size';

/** The parts of the active plan, one at a time (#252). */
type PlanTab = 'changes' | 'impact' | 'materials' | 'reservations' | 'add';

/**
 * Plans (#24) as a workspace (#252): an overview of the active plan with its next step, what needs attention first,
 * and tabs for its changes as a table, its impact, its reservations and adding to it; below, the list of plans and a
 * short row for a new one. Choosing a plan switches the whole app to its view.
 */
@Component({
  selector: 'cmdb-plan-panel',
  imports: [
    ToolSizeComponent,
    StatusComponent,
    PlanCreateComponent,
    PlanClassificationComponent,
    PlanRouteComponent,
    PlanChangesComponent,
    PlanImpactComponent,
    PlanMaterialsComponent,
    PlanListComponent,
  ],
  template: `
    <header class="head">
      <h2>Planer</h2>
      <cmdb-tool-size />
      <button type="button" class="close" aria-label="Stäng planer" (click)="tools.close()">
        ×
      </button>
    </header>
    <div class="body">
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
      @if (active.id() !== null) {
        @if (current(); as d) {
          <section class="overview" aria-label="Aktiv plan">
            <div class="title">
              <h3>{{ d.plan.name }}</h3>
              <cmdb-status [value]="lifecycle(d.plan)" [text]="statusLabels[d.plan.status]" />
            </div>
            <p class="muted">
              Skapad av {{ d.plan.createdBy }}
              @if (d.plan.createdVia === 'mcp') {
                · <strong>föreslagen av agent, granska innan införande</strong>
              }
              @if (d.plan.appliedBy) {
                · införd av {{ d.plan.appliedBy }}
              }
            </p>
            @if (d.plan.description) {
              <p>{{ d.plan.description }}</p>
            }
            @if (d.dependencies.length) {
              <p class="muted">
                Bygger på:
                @for (dep of d.dependencies; track dep.id; let last = $last) {
                  <button type="button" class="linkish" (click)="active.activate(dep.id)">
                    {{ dep.name }}</button
                  >{{ last ? '' : ', ' }}
                }
              </p>
            }
            <ul class="counts" aria-label="Ändringar per slag">
              <li>
                <strong>{{ total() }}</strong> {{ total() === 1 ? 'ändring' : 'ändringar' }}
              </li>
              @for (c of categories; track c) {
                @if (counts()[c]) {
                  <li>
                    <button type="button" class="linkish" (click)="show(c)">
                      {{ counts()[c] }} {{ categoryLabels[c].toLowerCase() }}
                    </button>
                  </li>
                }
              }
            </ul>
            <div class="buttons">
              @switch (step()) {
                @case ('apply') {
                  <button type="button" class="primary" [disabled]="busy()" (click)="apply(d.plan)">
                    För in i produktion
                  </button>
                }
                @case ('resolve') {
                  <button type="button" class="primary" (click)="show('attention')">
                    Lös konflikter
                  </button>
                }
                @case ('add') {
                  <button type="button" class="primary" (click)="tab.set('add')">
                    Lägg till ändringar
                  </button>
                }
              }
              @if (d.plan.status === 'draft') {
                @if (step() !== 'apply') {
                  <button
                    type="button"
                    class="action"
                    [disabled]="busy() || !d.plan.operations"
                    (click)="apply(d.plan)"
                  >
                    För in i produktion
                  </button>
                }
                <button type="button" class="action" [disabled]="busy()" (click)="cancel(d.plan)">
                  Avbryt planen
                </button>
              }
              <button
                type="button"
                [class]="step() === 'production' ? 'primary' : 'action'"
                (click)="active.activate(null)"
              >
                Visa produktion
              </button>
            </div>
          </section>

          @if (d.plan.flag || counts().attention) {
            <section class="attention" aria-label="Att åtgärda">
              <h4>Att åtgärda</h4>
              @if (d.plan.flag) {
                <p class="flag" role="status">{{ d.plan.flag }}</p>
              }
              @if (counts().attention) {
                <p>
                  {{ counts().attention }}
                  {{ counts().attention === 1 ? 'ändring passar' : 'ändringar passar' }} inte längre
                  produktionen eller krockar med andras anspråk.
                  <button type="button" class="action" (click)="show('attention')">Visa</button>
                </p>
              }
            </section>
          }
          @if (d.plan.status === 'draft') {
            <cmdb-plan-classification [planId]="d.plan.id" />
          }
          @if (d.plan.exception) {
            <p class="muted">Infört med undantag: {{ d.plan.exception }}</p>
          }

          @if (active.pending(); as p) {
            <p class="pending" role="status">
              Välj port att koppla till <span class="mono">{{ p.label }}</span> i
              utrustningspanelen.
              <button type="button" class="action" (click)="active.pending.set(null)">
                Avbryt
              </button>
            </p>
          }

          <div class="tabs" role="tablist" aria-label="Planens delar">
            @for (t of tabs(); track t.id) {
              <button
                type="button"
                role="tab"
                [attr.aria-selected]="tab() === t.id"
                (click)="tab.set(t.id)"
              >
                {{ t.label }}
                @if (t.count !== null) {
                  <span class="n">{{ t.count }}</span>
                }
              </button>
            }
          </div>

          <div role="tabpanel" class="panel">
            @switch (tab()) {
              @case ('changes') {
                @if (view(); as v) {
                  @for (group of groups(); track group.plan.id) {
                    @if (group.plan.id === d.plan.id) {
                      <cmdb-plan-changes
                        [changes]="group.changes"
                        [sites]="v.sites"
                        [editable]="d.plan.status === 'draft'"
                        [(filter)]="filter"
                        empty="Inga ändringar ännu. Lägg till från objekten, kartans högerklick eller fliken Lägg till."
                        (removed)="remove($event)"
                      />
                      @if (group.total > group.changes.length) {
                        <p class="muted">
                          Visar {{ group.changes.length }} av {{ group.total }}: de första och alla
                          med problem.
                          <button type="button" class="action" (click)="active.all.set(true)">
                            Visa alla
                          </button>
                        </p>
                      }
                    }
                  }
                  @for (group of groups(); track group.plan.id) {
                    @if (group.plan.id !== d.plan.id) {
                      <details class="dependency">
                        <summary>
                          Från {{ group.plan.name }} <span class="muted">({{ group.total }})</span>
                        </summary>
                        <cmdb-plan-changes [changes]="group.changes" [sites]="v.sites" />
                      </details>
                    }
                  }
                  <p class="muted">Vyn laddades på {{ v.elapsedMs }} ms.</p>
                }
              }
              @case ('impact') {
                <cmdb-plan-impact [planId]="d.plan.id" />
              }
              @case ('materials') {
                <cmdb-plan-materials [planId]="d.plan.id" [planName]="d.plan.name" />
              }
              @case ('reservations') {
                <ul class="ops">
                  @for (r of held(); track r.id) {
                    <li>
                      <span>{{ r.label }}</span>
                      @if (r.reason) {
                        <span class="muted"> – {{ r.reason }}</span>
                      }
                      @if (d.plan.status === 'draft') {
                        <button
                          type="button"
                          class="action"
                          [attr.aria-label]="'Släpp reservationen av ' + r.label"
                          (click)="release(r.id)"
                        >
                          Släpp
                        </button>
                      }
                    </li>
                  } @empty {
                    <li class="muted">
                      Inga. Reservera portar och fibrer från utrustnings- och kabelpanelerna.
                    </li>
                  }
                </ul>
              }
              @case ('add') {
                <cmdb-plan-create />
                <cmdb-plan-route [planId]="d.plan.id" />
              }
            }
          </div>
        } @else if (detail.error()) {
          <p class="error">Planen finns inte eller syns inte för dig.</p>
          <button type="button" class="action" (click)="active.activate(null)">
            Visa produktion
          </button>
        }
      }

      <cmdb-plan-list />
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
      &:hover {
        background: var(--surface-2);
        color: var(--text);
      }
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: var(--space-3) var(--space-4) var(--space-4);
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }
    h3 {
      margin: 0;
      font-size: var(--text-md);
    }
    h4 {
      margin: var(--space-2) 0 var(--space-1);
      font-size: var(--text-xs);
      font-weight: 600;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--text-muted);
    }
    p {
      margin: 0 0 var(--space-2);
    }
    .title {
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }
    .overview {
      border-left: 3px solid var(--status-planned);
      padding-left: var(--space-3);
      .title {
        justify-content: space-between;
        flex-wrap: wrap;
      }
    }
    .counts {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1) var(--space-3);
      list-style: none;
      margin: 0 0 var(--space-3);
      padding: 0;
      font-size: var(--text-sm);
    }
    .attention {
      padding: var(--space-2) var(--space-3);
      border: 1px solid var(--status-conflict);
      border-radius: var(--radius-md);
      h4 {
        margin-top: 0;
        color: var(--status-conflict);
      }
      p:last-child {
        margin-bottom: 0;
      }
    }
    .tabs {
      display: flex;
      gap: var(--space-1);
      border-bottom: var(--line);
      button {
        height: 30px;
        padding: 0 var(--space-3);
        border: 0;
        border-bottom: 2px solid transparent;
        background: none;
        color: var(--text-muted);
        font: inherit;
        font-size: var(--text-sm);
        cursor: pointer;
        &[aria-selected='true'] {
          color: var(--text);
          border-bottom-color: var(--focus);
          font-weight: 600;
        }
        &:hover {
          color: var(--text);
        }
      }
      .n {
        margin-left: var(--space-1);
        color: var(--text-muted);
        font-variant-numeric: tabular-nums;
      }
    }
    .panel {
      margin-top: calc(-1 * var(--space-2));
    }
    .dependency {
      margin-top: var(--space-2);
      summary {
        cursor: pointer;
        font-size: var(--text-sm);
      }
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .flag {
      color: var(--status-conflict);
      font-size: var(--text-sm);
    }
    .pending {
      background: var(--surface-2);
      padding: var(--space-2);
      border-radius: var(--radius-sm);
    }
    .error {
      color: var(--status-conflict);
    }
    .mono {
      font-family: var(--font-mono);
    }
    .ops {
      margin: 0;
      padding-left: var(--space-5);
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
    }
    .ops li {
      font-size: var(--text-sm);
    }
    .ops li {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex-wrap: wrap;
    }
    .buttons {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
    .primary {
      height: 30px;
      padding: 0 var(--space-4);
      border: 0;
      border-radius: var(--radius-md);
      background: var(--action);
      color: var(--action-text);
      font-weight: 600;
      cursor: pointer;
      &:disabled {
        opacity: 0.6;
        cursor: progress;
      }
    }
    .action {
      height: 24px;
      padding: 0 var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
      &:hover {
        background: var(--surface-2);
      }
    }
    .linkish {
      border: 0;
      background: none;
      padding: 0;
      color: var(--link);
      cursor: pointer;
      text-decoration: underline;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlanPanelComponent {
  private readonly http = inject(HttpClient);
  protected readonly tools = inject(Tools);
  protected readonly active = inject(ActivePlan);

  protected readonly statusLabels = planStatusLabels;
  protected readonly lifecycle = planLifecycle;

  protected readonly detail = httpResource<PlanDetail>(() => {
    const id = this.active.id();
    return id === null ? undefined : this.active.request(`/api/plans/${id}`);
  });
  protected readonly reservations = httpResource<ReservationView[]>(() => {
    const id = this.active.id();
    return id === null ? undefined : this.active.request(`/api/reservations?plan=${id}`);
  });
  /** The resources' values, or nothing while loading or failed: value() throws in the error state. */
  protected readonly current = computed(() =>
    this.detail.hasValue() ? this.detail.value() : null,
  );
  protected readonly view = computed(() =>
    this.active.view.hasValue() ? this.active.view.value() : null,
  );
  protected readonly held = computed(() =>
    this.reservations.hasValue() ? this.reservations.value() : [],
  );
  protected readonly groups = computed(() => {
    const view = this.view();
    return view ? byPlan(view) : [];
  });

  protected readonly categories = changeCategories;
  protected readonly categoryLabels = changeCategoryLabels;
  protected readonly tab = signal<PlanTab>('changes');
  protected readonly filter = signal<ChangeCategory | 'attention' | null>(null);

  /** The active plan's own changes, as listed in its view. */
  private readonly own = computed(
    () => this.groups().find((g) => g.plan.id === this.active.id()) ?? null,
  );
  protected readonly counts = computed(() => changeCounts(this.own()?.changes ?? []));
  protected readonly total = computed(
    () => this.own()?.total ?? this.current()?.plan.operations ?? 0,
  );
  protected readonly step = computed(() => {
    const plan = this.current()?.plan;
    return plan ? nextStep(plan, this.counts().attention) : 'production';
  });
  protected readonly tabs = computed(() => {
    const draft = this.current()?.plan.status === 'draft';
    const list: { id: PlanTab; label: string; count: number | null }[] = [
      { id: 'changes', label: 'Ändringar', count: this.total() },
      { id: 'impact', label: 'Påverkan', count: null },
      { id: 'materials', label: 'Material', count: null },
      { id: 'reservations', label: 'Reservationer', count: this.held().length },
    ];
    return draft ? [...list, { id: 'add' as const, label: 'Lägg till', count: null }] : list;
  });

  constructor() {
    // Another plan starts on its changes, unfiltered.
    effect(() => {
      this.active.id();
      untracked(() => {
        this.tab.set('changes');
        this.filter.set(null);
      });
    });
    // A change prefilled elsewhere (the rack view, the map's context menu) opens the form that takes it.
    const draft = inject(PlanDraft);
    effect(() => {
      if (draft.equipment() || draft.tab() || draft.cableFrom()) {
        untracked(() => this.tab.set('add'));
      }
    });
  }

  /** The changes tab, filtered to one kind or to those needing attention. */
  protected show(filter: ChangeCategory | 'attention'): void {
    this.filter.set(filter);
    this.tab.set('changes');
  }

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async remove(operationId: number): Promise<void> {
    const id = this.active.id();
    await this.run(() =>
      firstValueFrom(this.http.delete(`/api/plans/${id}/operations/${operationId}`)),
    );
  }

  protected async release(reservationId: number): Promise<void> {
    await this.run(() => firstValueFrom(this.http.delete(`/api/reservations/${reservationId}`)));
  }

  protected async apply(plan: PlanSummary): Promise<void> {
    if (!confirm(`För in "${plan.name}" i produktion? Ändringarna görs direkt i nätet.`)) {
      return;
    }
    await this.run(async () => {
      const post = (exception?: string) =>
        firstValueFrom(
          this.http.post<ApplyResult>(
            `/api/plans/${plan.id}/apply${exception ? `?exception=${encodeURIComponent(exception)}` : ''}`,
            null,
          ),
        );
      let result: ApplyResult;
      try {
        result = await post();
      } catch (e: unknown) {
        // The plan makes requirements of a classification level unmet (#179): applying it takes a reason, which is kept.
        const detail = (e as { status?: number; error?: { detail?: string } }).error?.detail;
        if ((e as { status?: number }).status !== 409 || !detail?.includes('undantag')) {
          throw e;
        }
        const reason = prompt(`${detail}

Motivering för undantaget:`)?.trim();
        if (!reason) {
          return;
        }
        result = await post(reason);
      }
      this.report(result, 'behöver ses över efter införandet');
    });
  }

  protected async cancel(plan: PlanSummary): Promise<void> {
    if (!confirm(`Avbryt "${plan.name}"? Planer som bygger på den flaggas.`)) {
      return;
    }
    await this.run(async () => {
      const result = await firstValueFrom(
        this.http.post<ApplyResult>(`/api/plans/${plan.id}/cancel`, null),
      );
      this.report(result, 'flaggades');
    });
  }

  private report(result: ApplyResult, what: string): void {
    if (result.flagged.length) {
      this.error.set(`${result.flagged.map((p) => p.name).join(', ')} ${what}.`);
    }
  }

  private async run(work: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
    } catch (e: unknown) {
      this.error.set(message(e));
    } finally {
      this.busy.set(false);
      this.active.changed();
    }
  }
}

function message(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    const body = e.error as { detail?: string; errors?: Record<string, string[]> } | null;
    return body?.detail ?? (body?.errors ? Object.values(body.errors).flat().join(' ') : e.message);
  }
  return String(e);
}
