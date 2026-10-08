import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import { CatalogImages } from './catalog-images';
import { FrontPanelBenchmark, FrontPanelView } from './front-panel-view';
import {
  drawsImage,
  imageSides,
  PanelPort,
  portInDirection,
  PortStatus,
  portStatus,
  portStatusLabels,
} from './front-panel-model';
import { PanelImages, PanelSide } from './models';

const CELL = 20;
const GAP = 4;
const PAD = 8;

const sideLabels: Record<PanelSide, string> = { front: 'Framsida', back: 'Baksida' };

/**
 * An equipment front panel drawn in SVG from the type's port template (#18): one cell per port at its row and
 * column, coloured by status and always explained by the legend's dot and text. When the catalog has a picture of
 * the model (#214) the picture is drawn instead, with each port an area on it, and a model with ports at the back
 * can be turned over. Click selects a port, Shift-click marks a range (for mass provisioning later), arrows move,
 * Enter or Space selects.
 */
@Component({
  selector: 'cmdb-front-panel',
  template: `
    @if (sides().length > 1) {
      <div class="sides" role="group" aria-label="Sida">
        @for (s of sides(); track s) {
          <button type="button" [attr.aria-pressed]="s === side()" (click)="side.set(s)">
            {{ sideLabels[s] }}
          </button>
        }
      </div>
    }
    <svg
      role="grid"
      [attr.aria-label]="label()"
      [attr.viewBox]="'0 0 ' + width() + ' ' + height()"
      [class.on-image]="image()"
      [style.max-width.px]="image() ? null : width() * 1.6"
      (keydown)="onKey($event)"
    >
      @if (imageUrl(); as url) {
        <image
          [attr.href]="url"
          x="0"
          y="0"
          [attr.width]="width()"
          [attr.height]="height()"
          preserveAspectRatio="none"
        />
      } @else {
        <rect
          class="face"
          x="0.5"
          y="0.5"
          [attr.width]="width() - 1"
          [attr.height]="height() - 1"
          rx="3"
        />
      }
      @for (c of cells(); track c.port.terminalId) {
        <g
          role="gridcell"
          [attr.tabindex]="c.port.terminalId === focusId() ? 0 : -1"
          [attr.aria-selected]="c.port.terminalId === selectedId()"
          [attr.aria-label]="c.port.name + ', ' + labels[c.status]"
          [attr.data-terminal]="c.port.terminalId"
          [class]="'port ' + c.status"
          [class.selected]="c.port.terminalId === selectedId()"
          [class.marked]="markedNow().has(c.port.terminalId)"
          (click)="choose(c.port, $event.shiftKey)"
        >
          <title>{{ c.port.name }} · {{ labels[c.status] }}{{ c.peer }}</title>
          <rect [attr.x]="c.x" [attr.y]="c.y" [attr.width]="c.w" [attr.height]="c.h" rx="3" />
          @if (!image() && c.port.type.startsWith('rj')) {
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
    .sides {
      display: inline-flex;
      gap: var(--space-1);
      margin-bottom: var(--space-2);
      button {
        font: inherit;
        font-size: var(--text-xs);
        padding: var(--space-1) var(--space-2);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        background: var(--surface-1);
        color: var(--text);
        cursor: pointer;
      }
      button[aria-pressed='true'] {
        background: var(--surface-2);
        border-color: var(--focus);
      }
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
    /* On a picture the port shows through: status is a tint over it, a free port only an outline. */
    .on-image .port {
      rect {
        fill: transparent;
        stroke: var(--text-muted);
        fill-opacity: 0.65;
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
  private readonly catalogImages = inject(CatalogImages);
  private readonly view = inject(FrontPanelView);

  readonly ports = input.required<readonly PanelPort[]>();
  readonly rows = input.required<number>();
  readonly columns = input.required<number>();
  /** Pictures of the model from the catalog (#214); without them, or with ports not placed on them, the grid. */
  readonly images = input<PanelImages | null | undefined>(null);
  readonly selectedId = input<number | null>(null);
  readonly marked = input<ReadonlySet<number>>(new Set());
  /** A port was chosen; range is true for Shift. */
  readonly choosePort = output<{ port: PanelPort; range: boolean }>();
  /** The model's name, for the performance panel. */
  readonly model = input('');

  /** Marks set by the rendering benchmark, in place of the parent's while it runs. */
  private readonly benchMarks = signal<ReadonlySet<number> | null>(null);
  protected readonly markedNow = computed(() => this.benchMarks() ?? this.marked());

  constructor() {
    // While a picture is shown the performance panel can measure it (#214).
    const benchmark = (signal: AbortSignal) => this.benchmark(signal);
    effect(() => {
      if (this.image()) {
        this.view.renderBenchmark = benchmark;
      } else if (this.view.renderBenchmark === benchmark) {
        this.view.renderBenchmark = null;
      }
    });
    inject(DestroyRef).onDestroy(() => {
      if (this.view.renderBenchmark === benchmark) {
        this.view.renderBenchmark = null;
      }
    });
  }

  protected readonly labels = portStatusLabels;
  protected readonly sideLabels = sideLabels;

  protected readonly sides = computed(() =>
    drawsImage(this.images(), this.ports()) ? imageSides(this.images()) : [],
  );

  /** The side shown: the selected port's, else the one chosen, else the front. */
  protected readonly side = linkedSignal<
    { selected: PanelSide | undefined; sides: PanelSide[] },
    PanelSide
  >({
    source: () => ({
      selected: this.ports().find((p) => p.terminalId === this.selectedId())?.box?.side,
      sides: this.sides(),
    }),
    computation: ({ selected, sides }, previous) =>
      selected && sides.includes(selected)
        ? selected
        : previous && sides.includes(previous.value)
          ? previous.value
          : (sides[0] ?? 'front'),
  });

  /** The picture of the side shown, or null for the grid. */
  protected readonly image = computed(() =>
    this.sides().length ? (this.images()?.[this.side()] ?? null) : null,
  );

  protected readonly imageUrl = computed(() => {
    const image = this.image();
    return image ? this.catalogImages.url(image.file)() : null;
  });

  protected readonly width = computed(
    () => this.image()?.width ?? PAD * 2 + this.columns() * (CELL + GAP) - GAP,
  );
  protected readonly height = computed(
    () => this.image()?.height ?? PAD * 2 + this.rows() * (CELL + GAP) - GAP,
  );

  /** The ports on the side shown: all of them on the grid. */
  protected readonly shown = computed(() =>
    this.image() ? this.ports().filter((p) => p.box?.side === this.side()) : this.ports(),
  );

  protected readonly label = computed(
    () =>
      `Frontpanel, ${this.ports().length} portar` +
      (this.sides().length > 1 ? `, ${sideLabels[this.side()].toLowerCase()}` : ''),
  );

  protected readonly cells = computed(() => {
    const image = !!this.image();
    return this.shown().map((port) => ({
      port,
      status: portStatus(port),
      ...(image && port.box
        ? { x: port.box.x, y: port.box.y, w: port.box.width, h: port.box.height }
        : {
            x: PAD + port.column * (CELL + GAP),
            y: PAD + port.row * (CELL + GAP),
            w: CELL,
            h: CELL,
          }),
      peer: port.connections.length
        ? ` — ${port.connections.map((c) => c.peer.label).join(', ')}`
        : '',
    }));
  });

  // Every port counts, on both sides of the picture.
  protected readonly legend = computed(() => {
    const counts = new Map<PortStatus, number>();
    for (const port of this.ports()) {
      const status = portStatus(port);
      counts.set(status, (counts.get(status) ?? 0) + 1);
    }
    return (['free', 'connected', 'planned', 'reserved', 'conflict'] as const)
      .filter((s) => counts.has(s))
      .map((status) => ({ status, count: counts.get(status)! }));
  });

  /** The roving tab stop: the selected port when it is shown, or the first one shown. */
  protected readonly focusId = computed(() => {
    const shown = this.shown();
    const selected = this.selectedId();
    return shown.some((p) => p.terminalId === selected) ? selected : (shown[0]?.terminalId ?? null);
  });

  /**
   * Marks a run of a quarter of the ports, moved one port each frame across every side, and records the frame
   * times: each frame restyles the ports on the picture as a Shift-click range does.
   */
  private async benchmark(signal: AbortSignal): Promise<FrontPanelBenchmark> {
    const ports = [...this.ports()].sort((a, b) => a.position - b.position);
    const run = Math.max(1, Math.round(ports.length / 4));
    const sides = this.sides();
    const shown = this.side();
    const frames: number[] = [];
    try {
      for (const side of sides) {
        this.side.set(side);
        let last = await nextFrame();
        for (let i = 0; i < 60; i++) {
          signal.throwIfAborted();
          const start = i % ports.length;
          this.benchMarks.set(new Set(ports.slice(start, start + run).map((p) => p.terminalId)));
          const t = await nextFrame();
          frames.push(t - last);
          last = t;
        }
      }
    } finally {
      this.benchMarks.set(null);
      this.side.set(shown);
    }
    return { model: this.model(), ports: ports.length, frames };
  }

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
    const next = portInDirection(this.shown(), current, event.key, !!this.image());
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

const nextFrame = () => new Promise<number>((resolve) => requestAnimationFrame(resolve));
