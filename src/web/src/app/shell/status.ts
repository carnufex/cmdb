import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type Lifecycle =
  'planned' | 'under_construction' | 'in_service' | 'decommissioning' | 'removed' | 'conflict';

const labels: Record<Lifecycle, string> = {
  planned: 'Planerad',
  under_construction: 'Under byggnation',
  in_service: 'I drift',
  decommissioning: 'Under avveckling',
  removed: 'Borttagen',
  conflict: 'Konflikt',
};

/** Status is always a dot and text, never colour alone (WCAG 2.1 AA, docs/ux.md). */
@Component({
  selector: 'cmdb-status',
  template: `<span class="dot" aria-hidden="true"></span>{{ label() }}`,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      font-size: var(--text-sm);
      color: var(--text-muted);
      white-space: nowrap;
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--status-color);
    }
  `,
  host: { '[style.--status-color]': 'color()' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatusComponent {
  readonly value = input.required<Lifecycle>();

  protected readonly label = computed(() => labels[this.value()] ?? this.value());
  protected readonly color = computed(() => {
    const v = this.value();
    const token =
      v === 'under_construction' ? 'construction' : v === 'in_service' ? 'in-service' : v;
    return `var(--status-${token})`;
  });
}
