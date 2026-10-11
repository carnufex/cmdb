import { HttpClient, HttpErrorResponse, httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { StatusComponent } from '../shell/status';
import { ActivePlan } from './active-plan';
import { dependencyChoices, PlanSummary, planLifecycle, planStatusLabels } from './plan-model';

/**
 * The plans the user can see, and a new plan as a short row (#252): a name, and under "more" a description and the
 * plans it builds on. With no plans, it says what a plan is.
 */
@Component({
  selector: 'cmdb-plan-list',
  imports: [FormsModule, StatusComponent],
  template: `
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
    <section aria-label="Alla planer" class="list">
      <div class="list-head">
        <h4>{{ active.id() === null ? 'Planer' : 'Alla planer' }}</h4>
        @if (!creating() && list().length) {
          <button type="button" class="action" (click)="startCreating()">Ny plan</button>
        }
      </div>
      @if (creating()) {
        <form (ngSubmit)="create()" aria-label="Ny plan" class="new">
          <div class="row">
            <input
              name="name"
              [(ngModel)]="name"
              required
              maxlength="200"
              placeholder="Planens namn"
              aria-label="Planens namn"
            />
            <button type="submit" class="primary" [disabled]="!name.trim() || busy()">
              Skapa och visa
            </button>
            <button type="button" class="action" (click)="creating.set(false)">Avbryt</button>
          </div>
          <details>
            <summary>Beskrivning och vad den bygger på</summary>
            <label
              >Beskrivning
              <textarea name="description" [(ngModel)]="description" rows="2"></textarea>
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
          </details>
        </form>
      }
      @if (plans.hasValue()) {
        @if (list().length) {
          <ul class="plans">
            @for (p of list(); track p.id) {
              <li>
                <button
                  type="button"
                  class="plan"
                  [attr.aria-current]="p.id === active.id() ? 'true' : null"
                  (click)="active.activate(p.id)"
                >
                  <cmdb-status [value]="lifecycle(p)" [text]="statusLabels[p.status]" />
                  <span class="name">{{ p.name }}</span>
                  @if (p.createdVia === 'mcp') {
                    <span class="muted" title="Föreslagen av en agent">agent</span>
                  }
                  <span class="muted">{{ p.operations }} ändr.</span>
                </button>
              </li>
            }
          </ul>
        } @else if (!creating()) {
          <div class="empty">
            <p>
              En plan samlar ändringar i nätet innan de görs: nya siter och kablar, kopplingar,
              flyttar och borttag. Hela appen visar nätet som det blir med planen. Konflikter med
              andra planer och reservationer syns direkt, och planen förs in i produktion i ett steg
              eller kastas.
            </p>
            <button type="button" class="primary" (click)="startCreating()">Ny plan</button>
          </div>
        }
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
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
    .list-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
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
    .new {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      margin-bottom: var(--space-3);
      .row {
        display: flex;
        gap: var(--space-2);
        input {
          flex: 1;
          min-width: 0;
        }
      }
      summary {
        cursor: pointer;
        color: var(--text-muted);
        font-size: var(--text-sm);
      }
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
    input,
    textarea {
      font: inherit;
      padding: var(--space-1) var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-1);
      color: var(--text);
    }
    .empty {
      padding: var(--space-3);
      border: var(--line);
      border-radius: var(--radius-md);
      background: var(--surface-1);
      p {
        font-size: var(--text-sm);
      }
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .error {
      color: var(--status-conflict);
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
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlanListComponent {
  private readonly http = inject(HttpClient);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  protected readonly active = inject(ActivePlan);

  protected readonly statusLabels = planStatusLabels;
  protected readonly lifecycle = planLifecycle;

  protected readonly plans = httpResource<PlanSummary[]>(() => this.active.request('/api/plans'));
  /** The plans, or none while loading or failed: value() throws in the error state. */
  protected readonly list = computed(() => (this.plans.hasValue() ? this.plans.value() : []));
  protected readonly choices = computed(() => dependencyChoices(this.list()));

  protected readonly creating = signal(false);
  protected name = '';
  protected description = '';
  protected readonly dependsOn = signal<ReadonlySet<number>>(new Set());
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected startCreating(): void {
    this.creating.set(true);
    setTimeout(() =>
      this.host.nativeElement.querySelector<HTMLInputElement>('input[name=name]')?.focus(),
    );
  }

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
    this.busy.set(true);
    this.error.set(null);
    try {
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
      this.creating.set(false);
      this.active.activate(plan.id);
    } catch (e: unknown) {
      const body = e instanceof HttpErrorResponse ? (e.error as { detail?: string } | null) : null;
      this.error.set(body?.detail ?? 'Planen kunde inte skapas.');
    } finally {
      this.busy.set(false);
      this.active.changed();
    }
  }
}
