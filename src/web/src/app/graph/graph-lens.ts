import { HttpClient } from '@angular/common/http';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import Graph from 'graphology';
import forceAtlas2 from 'graphology-layout-forceatlas2';
import Sigma from 'sigma';
import { firstValueFrom, map } from 'rxjs';
import { PanelStack } from '../shell/panels';
import { ThemeStore } from '../shell/theme';
import {
  EXPAND_BATCH,
  Filters,
  layerLabels,
  Neighbourhood,
  nodeSizes,
  SiteGraphLevel,
  SiteGraphNode,
  siteTypeLabels,
} from './graph-model';

/** Kilometres from SWEREF metres: a readable starting layout before forces take over. */
const SCALE = 1 / 1000;

/**
 * The neighbourhood graph lens (#20): what is around a site, by cable and by circuits, drawn with Sigma (WebGL).
 * Expand one level at a time, go back, refocus, and filter by layer and site type. The focus is in the URL (?g=).
 */
@Component({
  selector: 'cmdb-graph-lens',
  template: `
    <div #target class="canvas"></div>
    @if (!focus()) {
      <p class="empty">Öppna en site, i kartan eller med Ctrl+K, för att se dess grannskap.</p>
    }
    <aside class="controls" aria-label="Grannskapsgraf">
      <div class="row">
        <button type="button" class="action" [disabled]="busy() || !frontier()" (click)="expand()">
          Expandera en nivå
        </button>
        <button type="button" class="action" [disabled]="busy() || depth() < 2" (click)="back()">
          Backa
        </button>
        <button
          type="button"
          class="action"
          [disabled]="busy() || !canRefocus()"
          (click)="refocus()"
        >
          Fokusera på vald
        </button>
      </div>
      <p class="stats">
        {{ counts().nodes }} siter · {{ counts().edges }} kanter · nivå {{ depth() }}
        @if (frontier() > 0) {
          · {{ frontier() }} att expandera
        }
        @if (busy()) {
          · hämtar…
        }
      </p>
      <fieldset>
        <legend>Lager</legend>
        @for (l of layers; track l) {
          <label>
            <input
              type="checkbox"
              [checked]="filters().layers.has(l)"
              (change)="toggle('layers', l)"
            />
            <span [class]="'swatch ' + l"></span>{{ layerLabels[l] }}
          </label>
        }
      </fieldset>
      <fieldset>
        <legend>Sitetyp</legend>
        @for (t of siteTypes; track t) {
          <label>
            <input
              type="checkbox"
              [checked]="filters().siteTypes.has(t)"
              (change)="toggle('siteTypes', t)"
            />
            {{ siteTypeLabels[t] }}
          </label>
        }
      </fieldset>
      <p class="hint">
        Klick öppnar siten. Dubbelklick expanderar den. Färg är status, storlek är sitetyp.
      </p>
    </aside>
  `,
  styles: `
    :host {
      position: absolute;
      inset: 0;
      display: block;
      background: var(--bg);
    }
    .canvas {
      position: absolute;
      inset: 0;
    }
    .empty {
      position: absolute;
      inset: 40% 0 auto;
      text-align: center;
      color: var(--text-muted);
    }
    .controls {
      position: absolute;
      left: var(--space-3);
      bottom: var(--space-3);
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      max-width: 340px;
      padding: var(--space-3);
      background: color-mix(in srgb, var(--surface-1) 94%, transparent);
      border: var(--line);
      border-radius: var(--radius-sm);
      font-size: var(--text-sm);
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
    .action {
      height: 24px;
      padding: 0 var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      cursor: pointer;
      &:hover:not(:disabled) {
        background: var(--surface-2);
      }
      &:disabled {
        color: var(--text-muted);
        cursor: default;
      }
    }
    .stats,
    .hint {
      margin: 0;
      color: var(--text-muted);
    }
    .hint {
      font-size: var(--text-xs);
    }
    fieldset {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1) var(--space-3);
      margin: 0;
      padding: 0;
      border: 0;
    }
    legend {
      width: 100%;
      margin-bottom: var(--space-1);
      font-size: var(--text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--text-muted);
    }
    label {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
    }
    .swatch {
      display: inline-block;
      width: 16px;
      border-top: 3px solid var(--text-muted);
      &.transmission {
        border-top-width: 2px;
        opacity: 0.8;
      }
      &.logical {
        border-top-width: 1px;
        opacity: 0.6;
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GraphLensComponent {
  private readonly http = inject(HttpClient);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly panels = inject(PanelStack);
  private readonly theme = inject(ThemeStore);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly target = viewChild.required<ElementRef<HTMLDivElement>>('target');

  protected readonly layers = ['physical', 'transmission', 'logical'] as const;
  protected readonly siteTypes = ['hub', 'aggregation', 'radio', 'cabinet', 'splice'] as const;
  protected readonly layerLabels = layerLabels;
  protected readonly siteTypeLabels = siteTypeLabels;

  private readonly g = toSignal(this.route.queryParamMap.pipe(map((q) => q.get('g'))), {
    initialValue: null,
  });

  /** The site in focus: ?g=, or else the site on top of the panel stack. */
  protected readonly focus = computed(() => {
    const g = Number(this.g());
    if (g > 0) {
      return g;
    }
    const top = this.panels.top();
    return top?.type === 'site' ? Number(top.id) : null;
  });

  protected readonly filters = signal<Filters>({
    layers: new Set(['physical', 'transmission', 'logical']),
    siteTypes: new Set(['hub', 'aggregation', 'radio', 'cabinet', 'splice']),
  });
  protected readonly busy = signal(false);
  /** Bumped whenever the explored graph changes, so derived counts recompute. */
  private readonly version = signal(0);

  private state: Neighbourhood | null = null;
  private graph = new Graph({ type: 'undirected', multi: true });
  private sigma?: Sigma;
  private visible = new Set<number>();

  protected readonly depth = computed(() => (this.version(), this.state?.depth ?? 0));
  protected readonly frontier = computed(() => {
    this.version();
    return this.state ? this.state.frontier(this.filters()).length : 0;
  });
  protected readonly counts = computed(() => {
    this.version();
    const f = this.filters();
    if (!this.state) {
      return { nodes: 0, edges: 0 };
    }
    const edges = [...this.state.edges.values()].filter((e) =>
      this.state!.edgeVisible(e, f),
    ).length;
    return { nodes: this.state.visibleNodes(f).size, edges };
  });
  protected readonly canRefocus = computed(() => {
    const top = this.panels.top();
    return top?.type === 'site' && Number(top.id) !== this.state?.focus && this.version() >= 0;
  });

  constructor() {
    afterNextRender(() => this.create());
    inject(DestroyRef).onDestroy(() => this.sigma?.kill());

    effect(() => {
      const focus = this.focus();
      untracked(() => void this.start(focus));
    });
    effect(() => {
      this.filters();
      this.theme.theme();
      untracked(() => this.refresh());
    });
    // The selected site (top of the panel stack) is highlighted.
    effect(() => {
      this.panels.top();
      untracked(() => this.sigma?.refresh({ skipIndexation: true }));
    });
  }

  private create(): void {
    this.readPalette();
    this.sigma = new Sigma(this.graph, this.target().nativeElement, {
      renderEdgeLabels: false,
      labelRenderedSizeThreshold: 7,
      labelFont: 'Inter, system-ui, sans-serif',
      labelColor: { attribute: 'labelColor' },
      defaultEdgeType: 'line',
      zIndex: true,
      // Hover and highlight labels in the theme's colours instead of Sigma's white box.
      defaultDrawNodeHover: (context, data, settings) => {
        const size = settings.labelSize;
        context.font = `${settings.labelWeight} ${size}px ${settings.labelFont}`;
        const label = data.label ?? '';
        const width = context.measureText(label).width + 8;
        const x = data.x + data.size + 3;
        context.fillStyle = this.palette.surface;
        context.strokeStyle = this.palette.edge;
        context.beginPath();
        context.roundRect(x, data.y - size / 2 - 4, width, size + 8, 3);
        context.fill();
        context.stroke();
        context.fillStyle = this.palette.text;
        context.fillText(label, x + 4, data.y + size / 3);
      },
      nodeReducer: (key, attrs) => {
        const id = Number(key);
        const palette = this.palette;
        const selected = this.panels.top()?.type === 'site' && this.panels.top()!.id === key;
        return {
          ...attrs,
          hidden: !this.visible.has(id),
          color: selected ? palette.focus : attrs['color'],
          size: selected ? (attrs['size'] as number) + 3 : attrs['size'],
          highlighted: selected || id === this.state?.focus,
          labelColor: palette.text,
          zIndex: selected ? 2 : 1,
        };
      },
      edgeReducer: (_key, attrs) => ({
        ...attrs,
        hidden:
          !this.filters().layers.has(attrs['layer']) ||
          !this.visible.has(attrs['a']) ||
          !this.visible.has(attrs['b']),
        color: this.palette.edge,
      }),
    });
    this.sigma.on('clickNode', ({ node }) =>
      this.panels.open({ type: 'site', id: node }, { replace: true }),
    );
    this.sigma.on('doubleClickNode', (e) => {
      e.preventSigmaDefault();
      void this.expandNodes([Number(e.node)]);
    });
    void this.start(this.focus());
  }

  /** Colours from the design tokens, read once per theme: status for nodes, neutral lines for edges. */
  private palette = {
    focus: '',
    text: '',
    edge: '',
    surface: '',
    status: {} as Record<string, string>,
  };

  private readPalette(): void {
    const css = getComputedStyle(this.host.nativeElement);
    const v = (name: string) => css.getPropertyValue(name).trim();
    this.palette = {
      focus: v('--focus'),
      text: v('--text'),
      edge: v('--border-strong'),
      surface: v('--surface-2'),
      status: {
        planned: v('--status-planned'),
        under_construction: v('--status-construction'),
        in_service: v('--status-in-service'),
        decommissioning: v('--status-decommissioning'),
        removed: v('--status-removed'),
      },
    };
  }

  private statusColor(lifecycle: string): string {
    return this.palette.status[lifecycle] ?? this.palette.status['in_service'];
  }

  private loaded: number | null = null;

  private async start(focus: number | null): Promise<void> {
    if (!this.sigma || focus === this.loaded) {
      return;
    }
    this.loaded = focus;
    this.graph.clear();
    this.state = focus ? new Neighbourhood(focus) : null;
    this.version.update((v) => v + 1);
    if (focus) {
      await this.expandNodes([focus]);
      this.sigma.getCamera().animatedReset();
    }
  }

  protected async expand(): Promise<void> {
    if (!this.state) {
      return;
    }
    await this.expandNodes(this.state.frontier(this.filters()).slice(0, EXPAND_BATCH));
  }

  private async expandNodes(ids: number[]): Promise<void> {
    const state = this.state;
    if (!state || ids.length === 0 || this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      const levels = await Promise.all(
        ids.map((id) =>
          firstValueFrom(this.http.get<SiteGraphLevel>(`/api/sites/${id}/graph`)).catch(() => null),
        ),
      );
      if (state !== this.state) {
        return; // refocused meanwhile
      }
      const added = state.add(levels.filter((l): l is SiteGraphLevel => l !== null));
      for (const node of added.nodes) {
        this.addNode(node);
      }
      for (const edge of added.edges) {
        if (
          this.graph.hasNode(String(edge.source)) &&
          this.graph.hasNode(String(edge.target)) &&
          !this.graph.hasEdge(edge.id)
        ) {
          this.graph.addEdgeWithKey(edge.id, String(edge.source), String(edge.target), {
            layer: edge.layer,
            a: edge.source,
            b: edge.target,
            size: edge.layer === 'physical' ? 2 : edge.layer === 'transmission' ? 1.2 : 0.6,
          });
        }
      }
      this.layout();
      this.version.update((v) => v + 1);
      this.refresh();
    } finally {
      this.busy.set(false);
    }
  }

  private addNode(node: SiteGraphNode): void {
    if (this.graph.hasNode(String(node.id))) {
      return;
    }
    // Start from the map position (with a little jitter so equal points separate), then let forces settle it.
    this.graph.addNode(String(node.id), {
      x: node.x * SCALE + Math.random() * 0.5,
      y: node.y * SCALE + Math.random() * 0.5,
      size: nodeSizes[node.siteType] ?? 4,
      label: node.code,
      color: this.statusColor(node.lifecycle),
    });
  }

  private layout(): void {
    if (this.graph.order < 2) {
      return;
    }
    const settings = forceAtlas2.inferSettings(this.graph);
    forceAtlas2.assign(this.graph, {
      iterations: this.graph.order > 2000 ? 40 : 80,
      settings: { ...settings, barnesHutOptimize: this.graph.order > 500, gravity: 0.5 },
    });
  }

  protected back(): void {
    const removed = this.state?.back();
    if (!removed) {
      return;
    }
    removed.edges.forEach((id) => this.graph.hasEdge(id) && this.graph.dropEdge(id));
    removed.nodes.forEach(
      (id) => this.graph.hasNode(String(id)) && this.graph.dropNode(String(id)),
    );
    this.version.update((v) => v + 1);
    this.refresh();
  }

  protected refocus(): void {
    const top = this.panels.top();
    if (top?.type === 'site') {
      void this.router.navigate([], { queryParams: { g: top.id }, queryParamsHandling: 'merge' });
    }
  }

  protected toggle(kind: 'layers' | 'siteTypes', value: string): void {
    this.filters.update((f) => {
      const next = new Set(f[kind]);
      if (next.has(value)) {
        next.delete(value);
      } else {
        next.add(value);
      }
      return { ...f, [kind]: next };
    });
  }

  private refresh(): void {
    this.readPalette();
    this.visible = this.state ? this.state.visibleNodes(this.filters()) : new Set();
    this.graph.forEachNode((key, attrs) => {
      const node = this.state?.nodes.get(Number(key));
      if (node) {
        attrs['color'] = this.statusColor(node.lifecycle);
      }
    });
    this.sigma?.refresh();
  }
}
