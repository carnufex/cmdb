import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  PanelPort,
  portInDirection,
  PortStatus,
  portStatus,
  portStatusLabels,
} from './front-panel-model';

const CELL = 20;
const GAP = 4;
const PAD = 8;

/**
 * An equipment front panel drawn in SVG from the type's port template (#18): one cell per port at its row and
 * column, coloured by status and always explained by the legend's dot and text. Click selects a port, Shift-click
 * marks a range (for mass provisioning later), arrows move, Enter or Space selects.
 */
@Component({
  selector: 'cmdb-front-panel',
  template: `
    <svg
      role="grid"
      [attr.aria-label]="'Frontpanel, ' + ports().length + ' portar'"
      [attr.viewBox]="'0 0 ' + width() + ' ' + height()"
      [style.max-width.px]="width() * 1.6"
      (keydown)="onKey($event)"
    >
      <rect
        class="face"
        x="0.5"
        y="0.5"
        [attr.width]="width() - 1"
        [attr.height]="height() - 1"
        rx="3"
      />
      @for (c of cells(); track c.port.terminalId) {
        <g
          role="gridcell"
          [attr.tabindex]="c.port.terminalId === focusId() ? 0 : -1"
          [attr.aria-selected]="c.port.terminalId === selectedId()"
          [attr.aria-label]="c.port.name + ', ' + labels[c.status]"
          [attr.data-terminal]="c.port.terminalId"
          [class]="'port ' + c.status"
          [class.selected]="c.port.terminalId === selectedId()"
          [class.marked]="marked().has(c.port.terminalId)"
          (click)="choose(c.port, $event.shiftKey)"
        >
          <title>{{ c.port.name }} · {{ labels[c.status] }}{{ c.peer }}</title>
          <rect [attr.x]="c.x" [attr.y]="c.y" [attr.width]="cell" [attr.height]="cell" rx="3" />
          @if (c.port.type.startsWith('rj')) {
            <rect class="notch" [attr.x]="c.x + 6" [attr.y]="c.y + 13" width="8" height="4" />
          }
        </g>
      }
    </svg>
    <p class="legend">
      @for (s of legend(); track s.status) {
        <span class="item"
          ><span [class]="'dot ' + s.status"></span>{{ labels[s.status] }} {{ s.count }}</span
        >
      }
    </p>
  `,
  styles: `
    :host {
      display: block;
    }
    svg {
      display: block;
      width: 100%;
      height: auto;
      outline: none;
    }
    .face {
      fill: var(--surface-2);
      stroke: var(--border);
    }
    .port {
      cursor: pointer;
      outline: none;
      rect {
        fill: var(--surface-1);
        stroke: var(--border-strong);
        stroke-width: 1;
      }
      .notch {
        fill: var(--surface-2);
        stroke: none;
      }
      &.connected rect:not(.notch) {
        fill: var(--status-in-service);
        stroke: transparent;
      }
      &.planned rect:not(.notch) {
        fill: var(--status-planned);
        stroke: transparent;
      }
      &.reserved rect:not(.notch) {
        fill: transparent;
        stroke: var(--status-construction);
        stroke-width: 2;
        stroke-dasharray: 3 2;
      }
      &.conflict rect:not(.notch) {
        fill: var(--status-conflict);
        stroke: transparent;
      }
      &.marked rect:not(.notch) {
        stroke: var(--focus);
        stroke-width: 2;
      }
      &.selected rect:not(.notch),
      &:focus-visible rect:not(.notch) {
        stroke: var(--focus);
        stroke-width: 3;
      }
    }
    .legend {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1) var(--space-3);
      margin: var(--space-2) 0 0;
      font-size: var(--text-xs);
      color: var(--text-muted);
    }
    .item {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      border: 1px solid var(--border-strong);
      &.connected {
        background: var(--status-in-service);
        border-color: transparent;
      }
      &.planned {
        background: var(--status-planned);
        border-color: transparent;
      }
      &.reserved {
        border: 2px dashed var(--status-construction);
      }
      &.conflict {
        background: var(--status-conflict);
        border-color: transparent;
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FrontPanelComponent {
  readonly ports = input.required<readonly PanelPort[]>();
  readonly rows = input.required<number>();
  readonly columns = input.required<number>();
  readonly selectedId = input<number | null>(null);
  readonly marked = input<ReadonlySet<number>>(new Set());
  /** A port was chosen; range is true for Shift. */
  readonly choosePort = output<{ port: PanelPort; range: boolean }>();

  protected readonly cell = CELL;
  protected readonly labels = portStatusLabels;

  protected readonly width = computed(() => PAD * 2 + this.columns() * (CELL + GAP) - GAP);
  protected readonly height = computed(() => PAD * 2 + this.rows() * (CELL + GAP) - GAP);

  protected readonly cells = computed(() =>
    this.ports().map((port) => ({
      port,
      status: portStatus(port),
      x: PAD + port.column * (CELL + GAP),
      y: PAD + port.row * (CELL + GAP),
      peer: port.connections.length
        ? ` — ${port.connections.map((c) => c.peer.label).join(', ')}`
        : '',
    })),
  );

  protected readonly legend = computed(() => {
    const counts = new Map<PortStatus, number>();
    for (const c of this.cells()) {
      counts.set(c.status, (counts.get(c.status) ?? 0) + 1);
    }
    return (['free', 'connected', 'planned', 'reserved', 'conflict'] as const)
      .filter((s) => counts.has(s))
      .map((status) => ({ status, count: counts.get(status)! }));
  });

  /** The roving tab stop: the selected port, or the first one. */
  protected readonly focusId = computed(
    () => this.selectedId() ?? this.ports()[0]?.terminalId ?? null,
  );

  protected choose(port: PanelPort, range: boolean): void {
    this.choosePort.emit({ port, range });
  }

  protected onKey(event: KeyboardEvent): void {
    const id = Number((event.target as Element).getAttribute?.('data-terminal'));
    const current = this.ports().find((p) => p.terminalId === id);
    if (!current) {
      return;
    }
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.choose(current, event.shiftKey);
      return;
    }
    const next = portInDirection(this.ports(), current, event.key);
    const svg = event.currentTarget as Element;
    if (next) {
      event.preventDefault();
      this.choose(next, event.shiftKey);
      queueMicrotask(() =>
        (svg.querySelector(`[data-terminal="${next.terminalId}"]`) as SVGGElement | null)?.focus(),
      );
    }
  }
}
