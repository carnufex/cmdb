import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  model,
  output,
  signal,
} from '@angular/core';
import { MapView } from '../map/map-view';
import { ObjectLinkComponent } from '../objects/object-link';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import {
  ChangeCategory,
  changeCategories,
  changeCategory,
  changeCategoryLabels,
  changeCounts,
  ChangeQuery,
  changeRows,
  changeState,
  changeStateLabels,
  PlanDiff,
  PlanOperation,
} from './plan-model';

const stateLifecycle = {
  problem: 'conflict',
  blocked: 'conflict',
  conflict: 'under_construction',
  ok: 'planned',
} as const;

/**
 * The plan's changes as a table (#252): filter by kind or by what needs attention, search and sort. A row shows the
 * object with its icon, what changes and where it stands, with the reason when it does not fit; clicking it opens the
 * object and marks its sites in the map.
 */
@Component({
  selector: 'cmdb-plan-changes',
  imports: [ObjectLinkComponent, StatusComponent],
  template: `
    <div class="tools" role="toolbar" aria-label="Filtrera ändringarna">
      <button
        type="button"
        class="chip"
        [attr.aria-pressed]="filter() === null"
        (click)="filter.set(null)"
      >
        Alla <span class="n">{{ changes().length }}</span>
      </button>
      @if (counts().attention) {
        <button
          type="button"
          class="chip attention"
          [attr.aria-pressed]="filter() === 'attention'"
          (click)="filter.set('attention')"
        >
          Att åtgärda <span class="n">{{ counts().attention }}</span>
        </button>
      }
      @for (c of categories; track c) {
        @if (counts()[c]) {
          <button
            type="button"
            class="chip"
            [attr.aria-pressed]="filter() === c"
            (click)="filter.set(c)"
          >
            {{ labels[c] }} <span class="n">{{ counts()[c] }}</span>
          </button>
        }
      }
      <input
        type="search"
        placeholder="Sök i ändringarna"
        aria-label="Sök i ändringarna"
        [value]="text()"
        (input)="text.set($any($event.target).value)"
      />
      <select aria-label="Sortera" [value]="sort()" (change)="sort.set($any($event.target).value)">
        <option value="attention">Att åtgärda först</option>
        <option value="seq">I ordning</option>
        <option value="category">Efter slag</option>
        <option value="object">Efter objekt</option>
      </select>
    </div>
    <table>
      <thead>
        <tr>
          <th scope="col">Slag</th>
          <th scope="col">Objekt</th>
          <th scope="col">Ändring</th>
          <th scope="col">Status</th>
          @if (editable()) {
            <th scope="col"><span class="sr-only">Ta bort</span></th>
          }
        </tr>
      </thead>
      <tbody>
        @for (op of shown(); track op.id) {
          <tr
            [class.selected]="selected() === op.id"
            [class.attention]="state(op) !== 'ok'"
            tabindex="0"
            (click)="select(op)"
            (keydown.enter)="select(op)"
          >
            <td class="kind">{{ labels[category(op.kind)] }}</td>
            <td class="object">
              @if (op.target; as t) {
                <cmdb-link [ref]="t" />
              } @else {
                <span class="muted">–</span>
              }
            </td>
            <td>
              {{ op.summary }}
              @if (op.problem) {
                <span class="why">{{ op.problem }}</span>
              }
              @for (c of op.conflicts; track c) {
                <span class="why">{{ c }}</span>
              }
            </td>
            <td class="state">
              <cmdb-status [value]="lifecycle[state(op)]" [text]="stateLabels[state(op)]" />
            </td>
            @if (editable()) {
              <td>
                <button
                  type="button"
                  class="remove"
                  [attr.aria-label]="'Ta bort: ' + op.summary"
                  (click)="$event.stopPropagation(); removed.emit(op.id)"
                >
                  ×
                </button>
              </td>
            }
          </tr>
        } @empty {
          <tr>
            <td [attr.colspan]="editable() ? 5 : 4" class="muted">
              {{ changes().length ? 'Inga ändringar matchar.' : empty() }}
            </td>
          </tr>
        }
      </tbody>
    </table>
    @if (rows().length > shown().length) {
      <p class="more">
        Visar {{ shown().length }} av {{ rows().length }}.
        <button type="button" class="chip" (click)="limit.set(limit() + page)">Visa fler</button>
      </p>
    }
  `,
  styles: `
    :host {
      display: block;
      overflow-x: auto;
    }
    .tools {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1);
      margin-bottom: var(--space-2);
      input {
        flex: 1;
        min-width: 140px;
        height: 26px;
        padding: 0 var(--space-2);
        border: var(--line);
        border-radius: var(--radius-sm);
        background: var(--surface-1);
        color: var(--text);
        font: inherit;
        font-size: var(--text-sm);
      }
      select {
        height: 26px;
        border: var(--line);
        border-radius: var(--radius-sm);
        background: var(--surface-1);
        color: var(--text);
        font: inherit;
        font-size: var(--text-sm);
      }
    }
    .chip {
      height: 24px;
      padding: 0 var(--space-2);
      border: var(--line);
      border-radius: 12px;
      background: transparent;
      color: var(--text);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
      &[aria-pressed='true'] {
        background: var(--surface-2);
        border-color: var(--border-strong);
        font-weight: 600;
      }
      &.attention {
        color: var(--status-conflict);
      }
      .n {
        color: var(--text-muted);
        font-variant-numeric: tabular-nums;
      }
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-sm);
    }
    th {
      text-align: left;
      font-weight: 600;
      font-size: var(--text-xs);
      color: var(--text-muted);
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
    }
    td {
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
      vertical-align: top;
    }
    tbody tr {
      cursor: pointer;
      &:hover,
      &:focus-visible {
        background: var(--surface-2);
        outline: none;
      }
      &.selected {
        background: var(--surface-2);
        box-shadow: inset 3px 0 0 var(--focus);
      }
    }
    .kind {
      white-space: nowrap;
      color: var(--text-muted);
    }
    .object {
      white-space: nowrap;
    }
    .state {
      white-space: nowrap;
    }
    .why {
      display: block;
      color: var(--status-conflict);
    }
    .muted {
      color: var(--text-muted);
    }
    .more {
      margin: var(--space-2) 0 0;
      font-size: var(--text-sm);
      color: var(--text-muted);
    }
    .remove {
      border: 0;
      background: none;
      color: var(--text-muted);
      cursor: pointer;
      &:hover {
        color: var(--text);
      }
    }
    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlanChangesComponent {
  private readonly panels = inject(PanelStack);
  private readonly mapView = inject(MapView);

  readonly changes = input.required<readonly PlanOperation[]>();
  /** Where the plan's sites are, to mark a row's sites in the map. */
  readonly sites = input<PlanDiff['sites']>([]);
  readonly editable = input(false);
  readonly empty = input('Inga ändringar ännu.');
  readonly filter = model<ChangeCategory | 'attention' | null>(null);
  readonly removed = output<number>();

  protected readonly text = signal('');
  protected readonly sort = signal<ChangeQuery['sort']>('attention');
  protected readonly selected = signal<number | null>(null);

  protected readonly categories = changeCategories;
  protected readonly labels = changeCategoryLabels;
  protected readonly stateLabels = changeStateLabels;
  protected readonly lifecycle = stateLifecycle;
  protected readonly category = changeCategory;
  protected readonly state = changeState;

  protected readonly counts = computed(() => changeCounts(this.changes()));
  protected readonly rows = computed(() =>
    changeRows(this.changes(), { filter: this.filter(), text: this.text(), sort: this.sort() }),
  );
  /**
   * A page of rows at a time: two hundred rows with their links and status dots take most of the budget for switching
   * to a plan (docs/plan.md), fifty take a quarter.
   */
  protected readonly page = 50;
  protected readonly limit = linkedSignal({
    source: () => [this.filter(), this.text(), this.sort(), this.changes()] as const,
    computation: () => this.page,
  });
  protected readonly shown = computed(() => this.rows().slice(0, this.limit()));

  /** Opens the row's object and marks its sites in the map, which shows beside the table at half the screen. */
  protected select(op: PlanOperation): void {
    this.selected.set(op.id);
    const ids = new Set<number>(op.terminals.map((t) => t.site?.id ?? 0).filter((id) => id !== 0));
    if (op.target?.type === 'site') {
      ids.add(op.target.id);
    }
    const points = this.sites()
      .filter((s) => ids.has(s.id))
      .map((s) => [s.id, s.x, s.y] as [number, number, number]);
    if (points.length) {
      const xs = points.map((p) => p[1]);
      const ys = points.map((p) => p[2]);
      this.mapView.mark(points, [
        Math.min(...xs),
        Math.min(...ys),
        Math.max(...xs),
        Math.max(...ys),
      ]);
    }
    if (op.target && op.target.id > 0) {
      this.panels.open({ type: op.target.type, id: String(op.target.id) }, { replace: true });
    }
  }
}
