import { HttpClient, HttpErrorResponse, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ReservationView } from '../objects/claims';
import { ObjectLinkComponent } from '../objects/object-link';
import { StatusComponent } from '../shell/status';
import { Tools } from '../shell/tools';
import { ActivePlan } from './active-plan';
import {
  ApplyResult,
  byPlan,
  dependencyChoices,
  PlanDetail,
  PlanSummary,
  planLifecycle,
  planStatusLabels,
} from './plan-model';

/**
 * Plans (#24): the list, a new plan on top of others, and the active plan with its operations. Choosing a plan
 * switches the whole app to its view; operations are added from the objects themselves (ports in the equipment
 * panel) and listed here with anything that no longer fits production.
 */
@Component({
  selector: 'cmdb-plan-panel',
  imports: [FormsModule, StatusComponent, ObjectLinkComponent],
  template: `
    <header class="head">
      <h2>Planer</h2>
      <button type="button" class="close" aria-label="Stäng planer" (click)="tools.close()">
        ×
      </button>
    </header>
    <div class="body">
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
      @if (active.id() !== null) {
        @if (detail.value(); as d) {
          <section class="active" aria-label="Aktiv plan">
            <div class="title">
              <cmdb-status [value]="lifecycle(d.plan)" />
              <h3>{{ d.plan.name }}</h3>
            </div>
            <p class="muted">
              {{ statusLabels[d.plan.status] }} · {{ d.plan.createdBy }}
              @if (d.plan.appliedBy) {
                · införd av {{ d.plan.appliedBy }}
              }
            </p>
            @if (d.plan.description) {
              <p>{{ d.plan.description }}</p>
            }
            @if (d.plan.flag) {
              <p class="flag" role="status">{{ d.plan.flag }}</p>
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

            @if (active.pending(); as p) {
              <p class="pending" role="status">
                Välj port att koppla till <span class="mono">{{ p.label }}</span> i
                utrustningspanelen.
                <button type="button" class="action" (click)="active.pending.set(null)">
                  Avbryt
                </button>
              </p>
            }

            @if (active.view.value(); as v) {
              @for (group of groups(); track group.plan.id) {
                <h4>
                  {{ group.plan.id === d.plan.id ? 'Ändringar' : 'Från ' + group.plan.name }}
                  <span class="muted">({{ group.changes.length }})</span>
                </h4>
                <ol class="ops">
                  @for (op of group.changes; track op.id) {
                    <li [class.problem]="op.problem || op.conflicts.length">
                      <span>{{ op.summary }}</span>
                      @if (op.target) {
                        <cmdb-link [ref]="op.target" />
                      }
                      @if (op.problem) {
                        <span class="problem-text">{{ op.problem }}</span>
                      }
                      @for (c of op.conflicts; track c) {
                        <span class="problem-text"
                          >{{ c }}{{ op.blocked ? ' Stoppar införandet.' : '' }}</span
                        >
                      }
                      @if (group.plan.id === d.plan.id && d.plan.status === 'draft') {
                        <button
                          type="button"
                          class="remove"
                          [attr.aria-label]="'Ta bort: ' + op.summary"
                          (click)="remove(op.id)"
                        >
                          ×
                        </button>
                      }
                    </li>
                  } @empty {
                    <li class="muted">
                      Inga ändringar ännu. Koppla portar från utrustningspanelen.
                    </li>
                  }
                </ol>
              }
              <h4>
                Reservationer <span class="muted">({{ reservations.value()?.length ?? 0 }})</span>
              </h4>
              <ul class="ops">
                @for (r of reservations.value() ?? []; track r.id) {
                  <li>
                    <span>{{ r.label }}</span>
                    @if (r.reason) {
                      <span class="muted"> – {{ r.reason }}</span>
                    }
                    @if (d.plan.status === 'draft') {
                      <button
                        type="button"
                        class="remove"
                        [attr.aria-label]="'Släpp reservationen av ' + r.label"
                        (click)="release(r.id)"
                      >
                        ×
                      </button>
                    }
                  </li>
                } @empty {
                  <li class="muted">
                    Inga. Reservera portar och fibrer från utrustnings- och kabelpanelerna.
                  </li>
                }
              </ul>
              <p class="muted">Vyn laddades på {{ v.elapsedMs }} ms.</p>
            }

            <div class="buttons">
              @if (d.plan.status === 'draft') {
                <button type="button" class="primary" [disabled]="busy()" (click)="apply(d.plan)">
                  För in i produktion
                </button>
                <button type="button" class="action" [disabled]="busy()" (click)="cancel(d.plan)">
                  Avbryt planen
                </button>
              }
              <button type="button" class="action" (click)="active.activate(null)">
                Visa produktion
              </button>
            </div>
          </section>
        } @else if (detail.error()) {
          <p class="error">Planen finns inte eller syns inte för dig.</p>
          <button type="button" class="action" (click)="active.activate(null)">
            Visa produktion
          </button>
        }
      }

      <section aria-label="Alla planer">
        <h4>Alla planer</h4>
        <ul class="plans">
          @for (p of plans.value() ?? []; track p.id) {
            <li>
              <button
                type="button"
                class="plan"
                [attr.aria-current]="p.id === active.id() ? 'true' : null"
                (click)="active.activate(p.id)"
              >
                <cmdb-status [value]="lifecycle(p)" />
                <span class="name">{{ p.name }}</span>
                <span class="muted">{{ p.operations }} ändr.</span>
              </button>
            </li>
          } @empty {
            <li class="muted">Inga planer som du kan se.</li>
          }
        </ul>
      </section>

      <form (ngSubmit)="create()" aria-label="Ny plan">
        <h4>Ny plan</h4>
        <label>Namn <input name="name" [(ngModel)]="name" required maxlength="200" /></label>
        <label
          >Beskrivning <textarea name="description" [(ngModel)]="description" rows="2"></textarea>
        </label>
        @if (choices().length) {
          <fieldset>
            <legend>Bygger på</legend>
            @for (p of choices(); track p.id) {
              <label class="check"
                ><input
                  type="checkbox"
                  [checked]="dependsOn().has(p.id)"
                  (change)="toggleDependency(p.id)"
                />
                {{ p.name }}</label
              >
            }
          </fieldset>
        }
        <button type="submit" class="primary" [disabled]="!name.trim() || busy()">
          Skapa och visa
        </button>
      </form>
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
    .active {
      border-left: 3px solid var(--status-planned);
      padding-left: var(--space-3);
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .flag,
    .problem-text {
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
    .ops li.problem::marker {
      color: var(--status-conflict);
    }
    .ops li span + cmdb-link {
      margin-left: var(--space-1);
    }
    .problem-text {
      display: block;
    }
    .remove {
      margin-left: var(--space-1);
      border: 0;
      background: none;
      color: var(--text-muted);
      cursor: pointer;
      &:hover {
        color: var(--text);
      }
    }
    .plans {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .plan {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: 100%;
      padding: var(--space-1) var(--space-2);
      border: 0;
      border-radius: var(--radius-sm);
      background: none;
      color: inherit;
      text-align: left;
      cursor: pointer;
      &:hover {
        background: var(--surface-2);
      }
      &[aria-current='true'] {
        background: var(--surface-2);
        font-weight: 600;
      }
      .name {
        flex: 1;
      }
    }
    form {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }
    label {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      font-size: var(--text-sm);
    }
    label.check {
      flex-direction: row;
      align-items: center;
    }
    fieldset {
      border: 0;
      margin: 0;
      padding: 0;
    }
    legend {
      font-size: var(--text-sm);
      color: var(--text-muted);
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
    input,
    textarea {
      font: inherit;
      padding: var(--space-1) var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-1);
      color: var(--text);
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

  protected readonly plans = httpResource<PlanSummary[]>(() => this.active.request('/api/plans'));
  protected readonly detail = httpResource<PlanDetail>(() => {
    const id = this.active.id();
    return id === null ? undefined : this.active.request(`/api/plans/${id}`);
  });
  protected readonly reservations = httpResource<ReservationView[]>(() => {
    const id = this.active.id();
    return id === null ? undefined : this.active.request(`/api/reservations?plan=${id}`);
  });
  protected readonly groups = computed(() => {
    const view = this.active.view.value();
    return view ? byPlan(view) : [];
  });
  protected readonly choices = computed(() => dependencyChoices(this.plans.value() ?? []));

  protected name = '';
  protected description = '';
  protected readonly dependsOn = signal<ReadonlySet<number>>(new Set());
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected toggleDependency(id: number): void {
    this.dependsOn.update((s) => {
      const next = new Set(s);
      if (!next.delete(id)) {
        next.add(id);
      }
      return next;
    });
  }

  protected async create(): Promise<void> {
    await this.run(async () => {
      const plan = await firstValueFrom(
        this.http.post<PlanSummary>('/api/plans', {
          name: this.name.trim(),
          description: this.description.trim(),
          dependsOn: [...this.dependsOn()],
        }),
      );
      this.name = '';
      this.description = '';
      this.dependsOn.set(new Set());
      this.active.activate(plan.id);
    });
  }

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
      const result = await firstValueFrom(
        this.http.post<ApplyResult>(`/api/plans/${plan.id}/apply`, null),
      );
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
