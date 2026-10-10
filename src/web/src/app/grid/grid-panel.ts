import { ScrollingModule } from '@angular/cdk/scrolling';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  OnDestroy,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MapView } from '../map/map-view';
import { ActivePlan } from '../plans/active-plan';
import { Tools } from '../shell/tools';
import {
  cellValue,
  EditColumn,
  editColumns,
  Edits,
  fillDown,
  GridResult,
  GridRow,
  paste,
  setCell,
  toOperations,
} from './grid-model';
import { Selection } from './selection';
import { ToolSizeComponent } from '../shell/tool-size';

const ROW_HEIGHT = 30;

/**
 * The spreadsheet mode (#27): a selection of sites, or the equipment on them, as an editable grid. Paste a block from
 * Excel at the active cell, fill down with Ctrl+D over a Shift-marked range, and put the changes into the active plan,
 * where each is checked against the model's attribute schema before anything reaches production.
 */
@Component({
  selector: 'cmdb-grid-panel',
  imports: [ToolSizeComponent, ScrollingModule],
  template: `
    <header class="head">
      <h2>Kalkylark</h2>
      <cmdb-tool-size />
      <button type="button" class="close" aria-label="Stäng kalkylarket" (click)="tools.close()">
        ×
      </button>
    </header>
    <div class="bar">
      <span class="muted">{{ selection.sites()?.label ?? 'Inget urval' }}</span>
      <button type="button" class="action" (click)="lasso()">
        {{ mapView.lassoing() ? 'Avbryt lasso' : 'Rita lasso i kartan' }}
      </button>
      <span class="kinds" role="radiogroup" aria-label="Rader">
        <button
          type="button"
          role="radio"
          [attr.aria-checked]="kind() === 'site'"
          (click)="kind.set('site')"
        >
          Siter
        </button>
        <button
          type="button"
          role="radio"
          [attr.aria-checked]="kind() === 'equipment'"
          (click)="kind.set('equipment')"
        >
          Utrustning
        </button>
      </span>
    </div>
    @if (mapView.lassoing()) {
      <p class="hint" role="status">
        Klicka runt siterna i kartan och dubbelklicka för att stänga lasson.
      </p>
    }
    @if (grid(); as g) {
      <div
        class="table"
        role="grid"
        [attr.aria-rowcount]="g.rows.length"
        (paste)="onPaste($event)"
        (keydown)="onKey($event)"
      >
        <div class="row header" role="row" [style.grid-template-columns]="template()">
          <span role="columnheader">Kod</span>
          @if (g.kind === 'equipment') {
            <span role="columnheader">Site</span>
            <span role="columnheader">Modell</span>
          }
          @for (c of columns(); track c.key) {
            <span role="columnheader">{{ c.label }}</span>
          }
        </div>
        <cdk-virtual-scroll-viewport [itemSize]="rowHeight" class="viewport">
          <div
            *cdkVirtualFor="let row of g.rows; let r = index; trackBy: track"
            class="row"
            role="row"
            [class.changed]="edits().has(row.id)"
            [class.failed]="failedRow() === r"
            [style.grid-template-columns]="template()"
          >
            <span class="mono" role="gridcell">{{ row.code }}</span>
            @if (g.kind === 'equipment') {
              <span role="gridcell">{{ row.site?.code }}</span>
              <span role="gridcell" class="muted">{{ row.model }}</span>
            }
            @for (c of columns(); track c.key; let ci = $index) {
              <span role="gridcell" [class.marked]="isMarked(r, ci)">
                @if (c.values) {
                  <select
                    [value]="value(row, c)"
                    (focus)="focus(r, ci, $event)"
                    (change)="set(row, c, $any($event.target).value)"
                    [attr.aria-label]="c.label + ' för ' + row.code"
                  >
                    <option value=""></option>
                    @for (v of c.values; track v) {
                      <option [value]="v">{{ v }}</option>
                    }
                  </select>
                } @else {
                  <input
                    [value]="value(row, c)"
                    (focus)="focus(r, ci, $event)"
                    (mousedown)="mark($event, r, ci)"
                    (change)="set(row, c, $any($event.target).value)"
                    [attr.aria-label]="c.label + ' för ' + row.code"
                  />
                }
              </span>
            }
          </div>
        </cdk-virtual-scroll-viewport>
      </div>
      <footer class="foot">
        <span class="muted">
          {{ g.rows.length }} rader{{ g.truncated ? ' (fler finns)' : '' }} ·
          {{ changes() }} ändringar
        </span>
        @if (plan.view.value()?.plan; as p) {
          <button
            type="button"
            class="primary"
            [disabled]="busy() || changes() === 0 || p.status !== 'draft'"
            (click)="toPlan()"
          >
            Lägg i planen {{ p.name }}
          </button>
        } @else {
          <span class="muted">Välj en plan under Planer för att spara ändringarna.</span>
        }
        <button
          type="button"
          class="action"
          [disabled]="changes() === 0"
          (click)="edits.set(empty)"
        >
          Ångra ändringar
        </button>
      </footer>
      <p class="muted help">
        Klistra in från Excel i markerad cell. Shift-klicka för att markera nedåt och fyll med
        Ctrl+D.
      </p>
    } @else if (loading()) {
      <p class="muted pad">Hämtar…</p>
    } @else {
      <p class="muted pad">
        Välj siter med avancerad sökning (Öppna som kalkylark) eller en lasso i kartan.
      </p>
    }
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
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
    .bar,
    .foot {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-4);
    }
    .kinds {
      display: inline-flex;
      gap: 2px;
    }
    .kinds button {
      border: var(--line);
      background: none;
      color: var(--text-muted);
      font: inherit;
      font-size: var(--text-sm);
      padding: 1px var(--space-2);
      cursor: pointer;
      &[aria-checked='true'] {
        color: var(--text);
        background: var(--surface-2);
      }
    }
    .table {
      flex: 1;
      min-height: 0;
      display: flex;
      flex-direction: column;
      overflow-x: auto;
      border-top: var(--line);
    }
    .viewport {
      flex: 1;
      min-height: 120px;
    }
    .row {
      display: grid;
      align-items: center;
      height: 30px;
      border-bottom: var(--line);
      font-size: var(--text-sm);
    }
    .row > span {
      padding: 0 var(--space-1);
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }
    .row.header {
      position: sticky;
      top: 0;
      background: var(--surface-1);
      font-weight: 600;
      color: var(--text-muted);
    }
    .row.changed {
      box-shadow: inset 3px 0 0 var(--status-planned);
    }
    .row.failed {
      box-shadow: inset 3px 0 0 var(--status-conflict);
    }
    .marked {
      background: var(--surface-2);
    }
    input,
    select {
      width: 100%;
      box-sizing: border-box;
      font: inherit;
      padding: 2px var(--space-1);
      border: 1px solid transparent;
      background: transparent;
      color: var(--text);
      &:focus {
        border-color: var(--focus);
        outline: none;
        background: var(--surface-1);
      }
    }
    .mono {
      font-family: var(--font-mono);
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .hint,
    .help,
    .pad {
      margin: 0;
      padding: 0 var(--space-4) var(--space-2);
    }
    .error {
      margin: 0;
      padding: 0 var(--space-4) var(--space-2);
      color: var(--status-conflict);
      font-size: var(--text-sm);
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
    }
    .primary {
      height: 28px;
      padding: 0 var(--space-4);
      border: 0;
      border-radius: var(--radius-md);
      background: var(--action);
      color: var(--action-text);
      font-weight: 600;
      cursor: pointer;
      &:disabled {
        opacity: 0.6;
        cursor: default;
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GridPanelComponent implements OnDestroy {
  private readonly http = inject(HttpClient);
  protected readonly tools = inject(Tools);
  protected readonly selection = inject(Selection);
  protected readonly mapView = inject(MapView);
  protected readonly plan = inject(ActivePlan);

  protected readonly rowHeight = ROW_HEIGHT;
  protected readonly empty: Edits = new Map();
  protected readonly kind = signal<'site' | 'equipment'>('equipment');
  protected readonly grid = signal<GridResult | null>(null);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly failedRow = signal<number | null>(null);
  protected readonly edits = signal<Edits>(new Map());
  protected readonly active = signal<{ row: number; column: number } | null>(null);
  protected readonly anchor = signal<{ row: number; column: number } | null>(null);

  protected readonly columns = computed(() => {
    const g = this.grid();
    return g ? editColumns(g) : [];
  });
  protected readonly template = computed(() => {
    const fixed = this.grid()?.kind === 'equipment' ? '10rem 7rem 9rem' : '9rem';
    return `${fixed} repeat(${this.columns().length}, minmax(8rem, 1fr))`;
  });
  protected readonly changes = computed(
    () => toOperations(this.grid()?.rows ?? [], this.edits()).length,
  );

  constructor() {
    // A new selection or row kind loads the grid; unsaved edits belong to the old one.
    effect(() => {
      const sites = this.selection.sites();
      const kind = this.kind();
      untracked(() => void this.load(sites?.ids ?? [], kind));
    });
    // A closed lasso becomes the selection.
    effect(() => {
      const lasso = this.mapView.lasso();
      if (lasso) {
        untracked(() => void this.fromLasso(lasso.ring));
      }
    });
  }

  ngOnDestroy(): void {
    this.mapView.lassoing.set(false);
  }

  protected track = (_: number, row: GridRow) => row.id;

  protected value(row: GridRow, column: EditColumn): string {
    return cellValue(row, column, this.edits());
  }

  protected set(row: GridRow, column: EditColumn, text: string): void {
    this.edits.update((e) => setCell(e, row, column, text));
    this.failedRow.set(null);
  }

  protected focus(row: number, column: number, event: FocusEvent): void {
    this.active.set({ row, column });
    if (
      !(event as FocusEvent & { shiftKey?: boolean }).shiftKey &&
      this.anchor()?.column !== column
    ) {
      this.anchor.set(null);
    }
  }

  /** Shift-click marks the range from the active cell down (or up) in the same column. */
  protected mark(event: MouseEvent, row: number, column: number): void {
    const active = this.active();
    if (event.shiftKey && active && active.column === column) {
      this.anchor.set(active);
      this.active.set({ row, column });
    } else {
      this.anchor.set(null);
    }
  }

  protected isMarked(row: number, column: number): boolean {
    const [anchor, active] = [this.anchor(), this.active()];
    if (!anchor || !active || anchor.column !== column || active.column !== column) {
      return false;
    }
    return row >= Math.min(anchor.row, active.row) && row <= Math.max(anchor.row, active.row);
  }

  protected onKey(event: KeyboardEvent): void {
    const [anchor, active, g] = [this.anchor(), this.active(), this.grid()];
    if (
      (event.ctrlKey || event.metaKey) &&
      event.key.toLowerCase() === 'd' &&
      anchor &&
      active &&
      g
    ) {
      event.preventDefault();
      this.edits.update((e) =>
        fillDown(e, g.rows, this.columns()[active.column], anchor.row, active.row),
      );
    }
  }

  /** A block with tabs or line breaks is pasted cell by cell from the active cell; plain text stays in the cell. */
  protected onPaste(event: ClipboardEvent): void {
    const text = event.clipboardData?.getData('text/plain') ?? '';
    const [active, g] = [this.active(), this.grid()];
    if (!active || !g || !/[\t\n]/.test(text.replace(/\r?\n$/, ''))) {
      return;
    }
    event.preventDefault();
    this.edits.update((e) => paste(e, g.rows, this.columns(), active, text));
  }

  protected lasso(): void {
    this.mapView.lassoing.update((on) => !on);
  }

  protected async toPlan(): Promise<void> {
    const g = this.grid();
    const planId = this.plan.id();
    if (!g || planId === null) {
      return;
    }
    const ops = toOperations(g.rows, this.edits());
    this.busy.set(true);
    this.error.set(null);
    this.failedRow.set(null);
    try {
      await firstValueFrom(
        this.http.post(`/api/plans/${planId}/operations/batch`, {
          operations: ops.map((o) => o.operation),
        }),
      );
      this.plan.changed();
      this.edits.set(new Map());
    } catch (e: unknown) {
      const detail =
        e instanceof HttpErrorResponse
          ? ((e.error as { detail?: string } | null)?.detail ?? e.message)
          : String(e);
      const failed = /Operation (\d+)/.exec(detail);
      if (failed) {
        const row = ops[Number(failed[1]) - 1]?.row;
        this.failedRow.set(row ?? null);
        this.error.set(`${g.rows[row!]?.code ?? ''}: ${detail.replace(/^Operation \d+: /, '')}`);
      } else {
        this.error.set(detail);
      }
    } finally {
      this.busy.set(false);
    }
  }

  private async fromLasso(ring: number[][]): Promise<void> {
    try {
      const within = await firstValueFrom(
        this.http.post<{ sites: number[]; truncated: boolean }>('/api/sites/within', {
          polygon: ring,
        }),
      );
      this.selection.sites.set({
        ids: within.sites,
        label: `${within.sites.length} siter i lasso${within.truncated ? ' (fler finns)' : ''}`,
      });
    } catch {
      this.error.set('Lasson kunde inte användas.');
    }
  }

  private async load(ids: readonly number[], kind: 'site' | 'equipment'): Promise<void> {
    this.edits.set(new Map());
    this.active.set(null);
    this.anchor.set(null);
    if (ids.length === 0) {
      this.grid.set(null);
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    try {
      this.grid.set(
        await firstValueFrom(this.http.post<GridResult>('/api/grid', { kind, siteIds: ids })),
      );
    } catch {
      this.grid.set(null);
      this.error.set('Urvalet kunde inte hämtas.');
    } finally {
      this.loading.set(false);
    }
  }
}
