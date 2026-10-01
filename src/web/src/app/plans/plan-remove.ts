import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { ActivePlan } from './active-plan';

/**
 * Removes a site, equipment or cable in the active plan (#172). The server refuses when circuits run through it, and
 * says why: the services would break.
 */
@Component({
  selector: 'cmdb-plan-remove',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (plan.view.value()?.plan?.status === 'draft') {
      <p class="remove">
        <button type="button" class="secondary" [disabled]="busy()" (click)="remove()">
          Ta bort i planen
        </button>
        @if (type() === 'site') {
          <span class="muted">Med utrustningen och kablarna som slutar här.</span>
        }
      </p>
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    }
  `,
  styles: `
    .remove {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      margin: var(--space-2) 0 0;
    }
    .secondary {
      padding: var(--space-1) var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-2);
      color: var(--text);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .error {
      color: var(--status-conflict);
      font-size: var(--text-sm);
    }
  `,
})
export class PlanRemoveComponent {
  readonly type = input.required<'site' | 'equipment' | 'cable'>();
  readonly objectId = input.required<number>();

  protected readonly plan = inject(ActivePlan);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async remove(): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.plan.add({ kind: 'remove', type: this.type(), objectId: this.objectId() });
    } catch (e: unknown) {
      const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
      this.error.set(
        body?.detail ??
          (Object.values(body?.errors ?? {})
            .flat()
            .join(' ') ||
            'Objektet kunde inte tas bort i planen.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
