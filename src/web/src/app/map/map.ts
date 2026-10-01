import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import OlMap from 'ol/Map';
import View from 'ol/View';
import { FeatureLike } from 'ol/Feature';
import MVT from 'ol/format/MVT';
import TileLayer from 'ol/layer/Tile';
import VectorTileLayer from 'ol/layer/VectorTile';
import VectorLayer from 'ol/layer/Vector';
import VectorSource from 'ol/source/Vector';
import Feature from 'ol/Feature';
import OlPoint from 'ol/geom/Point';
import LineString from 'ol/geom/LineString';
import Polygon from 'ol/geom/Polygon';
import Draw from 'ol/interaction/Draw';
import { Geometry } from 'ol/geom';
import { Circle, Fill, RegularShape, Stroke, Style, Text } from 'ol/style';
import TileState from 'ol/TileState';
import Attribution from 'ol/control/Attribution';
import XYZ from 'ol/source/XYZ';
import VectorTile from 'ol/VectorTile';
import VectorTileSource from 'ol/source/VectorTile';
import { Auth } from '../auth/auth';
import { RUNTIME_CONFIG } from '../config';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { ThemeStore } from '../shell/theme';
import {
  GRID,
  HOME_EXTENT,
  networkTileGrid,
  registerSweref,
  resolutions,
  SWEREF,
  tileUrl,
} from './map-grid';
import { createBasemap, esriTileUrl, parseBasemap } from './map-basemap';
import { createStyler, Palette, readPalette } from './map-style';
import { MapView, Operations, PlannedObjects, Route } from './map-view';

interface Hover {
  x: number;
  y: number;
  code: string;
  name: string | null;
}

/**
 * The map lens. OpenLayers is kept inside this component so the engine can be swapped (ADR-0004).
 * Network tiles come from the API with the user's token. The background is Esri's keyless basemap
 * (ADR-0010), which only ever sees the map extent, never network data; `basemap: none` turns it off.
 */
@Component({
  selector: 'cmdb-map',
  imports: [StatusComponent],
  templateUrl: './map.html',
  styleUrl: './map.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MapComponent {
  private readonly auth = inject(Auth);
  private readonly config = inject(RUNTIME_CONFIG);
  private readonly panels = inject(PanelStack);
  private readonly theme = inject(ThemeStore);
  private readonly mapView = inject(MapView);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly target = viewChild.required<ElementRef<HTMLDivElement>>('target');

  protected readonly hover = signal<Hover | null>(null);
  protected readonly zoom = signal(0);
  protected readonly detailZoom = GRID.detailZoom;

  private map?: OlMap;
  private network?: VectorTileLayer;
  private basemap?: TileLayer<XYZ>;
  private marks?: VectorLayer<VectorSource<Feature<OlPoint>>>;
  private route?: VectorLayer<VectorSource<Feature<Geometry>>>;
  private planned?: VectorLayer<VectorSource<Feature<Geometry>>>;
  private operations?: VectorLayer<VectorSource<Feature<Geometry>>>;
  private liveKey = '';
  private lassoDraw?: Draw;
  protected readonly operationsShown = signal(false);
  private palette?: Palette;

  constructor() {
    afterNextRender(() => this.create());
    inject(DestroyRef).onDestroy(() => {
      this.map?.setTarget(undefined);
      this.mapView.renderBenchmark = null;
    });

    // Re-read the palette when the theme changes, and redraw the selection when the panel stack changes.
    effect(() => {
      const theme = this.theme.theme();
      this.palette = undefined;
      this.restyle();
      this.basemap?.getSource()?.setUrl(esriTileUrl(theme));
    });
    effect(() => {
      this.panels.top();
      this.network?.changed();
    });
    // Advanced search marks its results; they get their own layer so they show at every zoom.
    effect(() => {
      const highlight = this.mapView.highlight();
      if (!this.marks || !this.map) {
        return;
      }
      this.showMarks(highlight?.points ?? [], highlight?.extent ?? null);
    });
    // A trace draws its route in its own layer, on top of everything (#19).
    effect(() => {
      const route = this.mapView.route();
      if (this.route && this.map) {
        this.showRoute(route);
      }
    });
    // A lasso (#27): the map draws a polygon until it is closed, then hands its ring on.
    effect(() => {
      const on = this.mapView.lassoing();
      if (!this.map) {
        return;
      }
      if (on && !this.lassoDraw) {
        this.lassoDraw = new Draw({ source: new VectorSource(), type: 'Polygon' });
        this.lassoDraw.on('drawend', (e) => {
          const ring = (e.feature.getGeometry() as Polygon).getCoordinates()[0];
          this.mapView.closeLasso(ring.map((p) => [Math.round(p[0]), Math.round(p[1])]));
        });
        this.map.addInteraction(this.lassoDraw);
      } else if (!on && this.lassoDraw) {
        this.map.removeInteraction(this.lassoDraw);
        this.lassoDraw = undefined;
      }
    });
    // What the active plan creates (#107): planned sites and cables, dashed in the planned colour.
    effect(() => {
      const planned = this.mapView.planned();
      if (this.planned && this.map) {
        this.showPlanned(planned);
      }
    });
    // The operations layer (#156): incidents, work, risks, and the live call's fault and what it takes down.
    effect(() => {
      const operations = this.mapView.operations();
      if (this.operations && this.map) {
        this.showOperations(operations);
      }
    });
    // Other parts of the app (search, panels) ask the map to go somewhere.
    effect(() => {
      const focus = this.mapView.focusRequest();
      const view = this.map?.getView();
      if (focus && view) {
        view.animate({
          center: [focus.x, focus.y],
          zoom: Math.max(view.getZoom() ?? 0, 8),
          duration: 400,
        });
      }
    });
  }

  private create(): void {
    registerSweref();
    const { styler, reset } = createStyler(
      () => (this.palette ??= readPalette(this.host.nativeElement)),
      (f) => this.isSelected(f),
    );
    this.restyle = () => {
      reset();
      this.network?.changed();
      this.marks?.changed();
      this.route?.changed();
      this.planned?.changed();
      this.operations?.changed();
    };

    this.network = new VectorTileLayer({
      source: new VectorTileSource({
        format: new MVT({ idProperty: 'id' }),
        projection: SWEREF,
        tileGrid: networkTileGrid(),
        tileUrlFunction: ([z, x, y]) => tileUrl(z, x, y),
        tileLoadFunction: (tile, url) => this.loadTile(tile as VectorTile<FeatureLike>, url),
      }),
      style: styler,
      declutter: false,
      // Vector mode redraws interim tiles as vectors while zooming, instead of scaling blurry images.
      renderMode: 'vector',
    });

    this.marks = new VectorLayer({
      source: new VectorSource<Feature<OlPoint>>(),
      style: () => this.markStyle(),
      zIndex: 10,
    });
    this.route = new VectorLayer({
      source: new VectorSource<Feature<Geometry>>(),
      style: (f) => this.routeStyle(f),
      zIndex: 20,
    });
    this.planned = new VectorLayer({
      source: new VectorSource<Feature<Geometry>>(),
      style: (f) => this.plannedStyle(f),
      zIndex: 15,
    });
    this.operations = new VectorLayer({
      source: new VectorSource<Feature<Geometry>>(),
      style: (f) => this.operationsStyle(f),
      zIndex: 18,
    });
    const layers: (TileLayer | VectorTileLayer | VectorLayer)[] = [
      this.network,
      this.marks,
      this.planned,
      this.operations,
      this.route,
    ];
    if (parseBasemap(this.config.basemap) === 'esri') {
      this.basemap = createBasemap(this.theme.theme());
      layers.unshift(this.basemap);
    }

    this.map = new OlMap({
      target: this.target().nativeElement,
      layers,
      controls: this.basemap ? [new Attribution({ collapsible: false })] : [],
      view: new View({
        projection: SWEREF,
        resolutions,
        constrainResolution: true,
        extent: [GRID.minX, GRID.minY, GRID.maxX, GRID.maxY],
      }),
    });
    this.map.getView().fit(HOME_EXTENT, { padding: [24, 24, 24, 24] });
    this.mapView.renderBenchmark = (signal) => this.benchmarkRendering(signal);
    const pending = this.mapView.highlight();
    if (pending) {
      this.showMarks(pending.points, pending.extent);
    }
    this.showRoute(this.mapView.route());
    this.showPlanned(this.mapView.planned());
    this.showOperations(this.mapView.operations());
    const reportCenter = () => {
      const [x, y] = this.map!.getView().getCenter() ?? [0, 0];
      this.mapView.center.set({ x, y });
    };
    reportCenter();
    this.map.on('moveend', reportCenter);
    this.zoom.set(Math.round(this.map.getView().getZoom() ?? 0));
    this.map
      .getView()
      .on('change:resolution', () => this.zoom.set(Math.round(this.map!.getView().getZoom() ?? 0)));

    this.map.on('pointermove', (e) => {
      if (e.dragging) {
        return;
      }
      const feature = this.map!.forEachFeatureAtPixel(e.pixel, (f) => f, { hitTolerance: 4 });
      this.target().nativeElement.style.cursor = feature ? 'pointer' : '';
      this.hover.set(
        feature && !feature.get('mark') && !feature.get('route')
          ? {
              x: e.pixel[0],
              y: e.pixel[1],
              code: String(feature.get('code')),
              name: feature.get('planned') ? 'Planerad' : (feature.get('name') ?? null),
            }
          : null,
      );
    });
    this.map.on('click', (e) => {
      const feature = this.map!.forEachFeatureAtPixel(e.pixel, (f) => f, { hitTolerance: 4 });
      if (this.mapView.picking()) {
        // Drawing a cable in a plan (#26): a site click picks the site, production or planned.
        if (feature && (feature.get('planned') === 'site' || feature.get('layer') === 'sites')) {
          this.mapView.pick(String(feature.get('code')));
        }
        return;
      }
      if (feature?.get('ops')) {
        // The operations layer (#156): incidents, risks and the live fault open their site.
        const site = feature.get('siteId') as number | undefined;
        if (site) {
          this.panels.open({ type: 'site', id: String(site) }, { replace: true });
        }
        return;
      }
      if (feature?.get('planned')) {
        // Planned in the active plan (#107): nothing in production to open; the plan panel lists it.
        return;
      } else if (feature?.get('route')) {
        // Part of an open trace: stack it on the trace instead of starting over.
        this.panels.open({ type: feature.get('route'), id: String(feature.getId()) });
      } else if (feature?.get('mark')) {
        this.panels.open({ type: 'site', id: String(feature.getId()) }, { replace: true });
      } else if (feature) {
        const type = feature.get('layer') === 'sites' ? 'site' : 'cable';
        this.panels.open({ type, id: String(feature.getId()) }, { replace: true });
      }
    });
  }

  private restyle: () => void = () => undefined;

  /**
   * Flies over the country through several zoom levels, loading and drawing tiles as a user would, and
   * records the time between animation frames. Returns to the starting view.
   */
  private async benchmarkRendering(signal: AbortSignal): Promise<number[]> {
    const view = this.map!.getView();
    const home = { center: view.getCenter()!, zoom: view.getZoom()! };
    const [minX, minY, maxX, maxY] = HOME_EXTENT;
    const stops: [number, number, number][] = [
      [0.5, 0.25, 6],
      [0.45, 0.55, 8],
      [0.6, 0.7, 10],
      [0.35, 0.85, 9],
      [0.55, 0.4, 7],
      [0.5, 0.5, 4],
    ];
    const frames: number[] = [];
    let last = performance.now();
    let running = true;
    const tick = (t: number) => {
      frames.push(t - last);
      last = t;
      if (running) {
        requestAnimationFrame(tick);
      }
    };
    requestAnimationFrame(tick);
    const fly = (center: number[], zoom: number) =>
      new Promise<void>((resolve) =>
        view.animate({ center, zoom, duration: 900 }, () => resolve()),
      );
    try {
      for (const [fx, fy, zoom] of stops) {
        signal.throwIfAborted();
        await fly([minX + fx * (maxX - minX), minY + fy * (maxY - minY)], zoom);
      }
    } finally {
      running = false;
      await fly(home.center, home.zoom);
    }
    // The first delta spans the time before the benchmark started.
    return frames.slice(1);
  }

  private markStyleCache?: { key: string; style: Style };

  private markStyle(): Style {
    const palette = (this.palette ??= readPalette(this.host.nativeElement));
    const key = `${palette.focus}|${palette.bg}`;
    if (this.markStyleCache?.key !== key) {
      this.markStyleCache = {
        key,
        style: new Style({
          image: new Circle({
            radius: 4.5,
            fill: new Fill({ color: palette.focus }),
            stroke: new Stroke({ color: palette.bg, width: 1.5 }),
          }),
        }),
      };
    }
    return this.markStyleCache.style;
  }

  private plannedStyleCache?: {
    key: string;
    line: Style[];
    point: Style;
    removedLine: Style[];
    removedPoint: Style;
  };

  private plannedStyle(feature: FeatureLike): Style | Style[] {
    const palette = (this.palette ??= readPalette(this.host.nativeElement));
    const key = `${palette.planned}|${palette.bg}|${palette.conflict}`;
    if (this.plannedStyleCache?.key !== key) {
      this.plannedStyleCache = {
        key,
        line: [
          new Style({ stroke: new Stroke({ color: palette.bg, width: 6 }) }),
          new Style({ stroke: new Stroke({ color: palette.planned, width: 3, lineDash: [8, 6] }) }),
        ],
        point: new Style({
          image: new Circle({
            radius: 6,
            fill: new Fill({ color: palette.bg }),
            stroke: new Stroke({ color: palette.planned, width: 3 }),
          }),
          zIndex: 1,
        }),
        // Underneath what replaces it: a split's new parts follow the same line.
        removedLine: [
          new Style({ stroke: new Stroke({ color: palette.bg, width: 6 }), zIndex: -1 }),
          new Style({
            stroke: new Stroke({ color: palette.conflict, width: 2.5, lineDash: [3, 6] }),
            zIndex: -1,
          }),
        ],
        removedPoint: new Style({
          image: new RegularShape({
            points: 4,
            radius: 7,
            radius2: 0,
            angle: Math.PI / 4,
            stroke: new Stroke({ color: palette.conflict, width: 3 }),
          }),
          zIndex: 2,
        }),
      };
    }
    switch (feature.get('planned')) {
      case 'site':
        return this.plannedStyleCache.point;
      case 'removed-cable':
        return this.plannedStyleCache.removedLine;
      case 'removed-site':
        return this.plannedStyleCache.removedPoint;
      default:
        return this.plannedStyleCache.line;
    }
  }

  /** Draws what the active plan creates; nothing to open, since planned objects have no panel yet. */
  private showPlanned(planned: PlannedObjects | null): void {
    const source = this.planned!.getSource()!;
    source.clear(true);
    if (!planned) {
      return;
    }
    source.addFeatures([
      ...planned.cables.map((c) => {
        const f = new Feature<Geometry>(new LineString(c.coordinates.map((p) => [p[0], p[1]])));
        f.set('planned', 'cable');
        f.set('code', c.code);
        return f;
      }),
      ...planned.sites.map((s) => {
        const f = new Feature<Geometry>(new OlPoint([s.x, s.y]));
        f.set('planned', 'site');
        f.set('code', s.code);
        return f;
      }),
      // Removed in the plan (#172), or replaced by a split (#168): struck out.
      ...(planned.removed?.cables ?? []).map((c) => {
        const f = new Feature<Geometry>(new LineString(c.coordinates.map((p) => [p[0], p[1]])));
        f.set('planned', 'removed-cable');
        f.set('code', `${c.code} (tas bort)`);
        return f;
      }),
      ...(planned.removed?.sites ?? []).map((s) => {
        const f = new Feature<Geometry>(new OlPoint([s.x, s.y]));
        f.set('planned', 'removed-site');
        f.set('code', 'Tas bort');
        return f;
      }),
    ]);
  }

  private operationsStyleCache?: { key: string; styles: Record<string, Style | Style[]> };

  /** Colour says status here too: red is down, amber is at risk or under work. */
  // Canvas colours: a token's #rrggbb with an alpha byte (canvas does not take color-mix()).
  private operationsStyle(feature: FeatureLike): Style | Style[] {
    const palette = (this.palette ??= readPalette(this.host.nativeElement));
    const key = `${palette.conflict}|${palette.decommissioning}|${palette.bg}`;
    if (this.operationsStyleCache?.key !== key) {
      const red = palette.conflict;
      const amber = palette.decommissioning;
      const halo = new Stroke({ color: palette.bg, width: 2 });
      this.operationsStyleCache = {
        key,
        styles: {
          work: new Style({
            fill: new Fill({ color: alpha(amber, 0.22) }),
            stroke: new Stroke({ color: amber, width: 2 }),
          }),
          'work-planned': new Style({
            fill: new Fill({ color: alpha(amber, 0.1) }),
            stroke: new Stroke({ color: amber, width: 2, lineDash: [6, 5] }),
          }),
          risk: new Style({
            image: new RegularShape({
              points: 3,
              radius: 8,
              fill: new Fill({ color: amber }),
              stroke: halo,
            }),
            zIndex: 2,
          }),
          incident: new Style({
            image: new Circle({ radius: 7, fill: new Fill({ color: red }), stroke: halo }),
            zIndex: 3,
          }),
          down: [
            new Style({ stroke: new Stroke({ color: palette.bg, width: 6 }) }),
            new Style({ stroke: new Stroke({ color: red, width: 3 }) }),
          ],
          'down-site': new Style({
            image: new Circle({ radius: 3.5, fill: new Fill({ color: red }), stroke: halo }),
            zIndex: 1,
          }),
          false: [
            new Style({ stroke: new Stroke({ color: palette.bg, width: 6 }) }),
            new Style({ stroke: new Stroke({ color: amber, width: 3, lineDash: [8, 6] }) }),
          ],
          'false-site': new Style({
            image: new Circle({ radius: 3.5, fill: new Fill({ color: amber }), stroke: halo }),
            zIndex: 1,
          }),
          fault: new Style({
            image: new Circle({
              radius: 13,
              fill: new Fill({ color: alpha(red, 0.25) }),
              stroke: new Stroke({ color: red, width: 3 }),
            }),
            zIndex: 4,
          }),
        },
      };
    }
    const kind = feature.get('ops') as string;
    const style = this.operationsStyleCache.styles[kind];
    const label = feature.get('label') as string | undefined;
    if (!label || Array.isArray(style)) {
      return style;
    }
    // Labels are per feature; the shared style keeps its symbol.
    return [
      style,
      new Style({
        text: new Text({
          text: label,
          offsetY: kind === 'fault' ? -24 : -16,
          font: '600 12px Inter Variable, system-ui, sans-serif',
          fill: new Fill({
            color:
              kind === 'fault' || kind === 'incident' ? palette.conflict : palette.decommissioning,
          }),
          stroke: new Stroke({ color: palette.bg, width: 4 }),
        }),
        zIndex: 5,
      }),
    ];
  }

  /** Draws the operations layer and, when the live call moves to a new object, frames it. */
  private showOperations(operations: Operations | null): void {
    const source = this.operations!.getSource()!;
    source.clear(true);
    this.operationsShown.set(operations !== null);
    if (!operations) {
      this.liveKey = '';
      return;
    }
    const point = (x: number, y: number, props: Record<string, unknown>) => {
      const f = new Feature<Geometry>(new OlPoint([x, y]));
      f.setProperties(props);
      return f;
    };
    const line = (coordinates: readonly (readonly number[])[], ops: string) => {
      const f = new Feature<Geometry>(new LineString(coordinates.map((p) => [p[0], p[1]])));
      f.set('ops', ops);
      return f;
    };
    const features: Feature<Geometry>[] = [
      ...operations.works.map((w) => {
        const f = new Feature<Geometry>(new Polygon([w.ring.map((p) => [p[0], p[1]])]));
        f.setProperties({
          ops: w.ongoing ? 'work' : 'work-planned',
          code: w.ongoing ? 'Pågående arbete' : 'Planerat arbete',
          name: `${w.title}, ${w.contractor}`,
        });
        return f;
      }),
      ...operations.risks.map((r) =>
        point(r.site.x, r.site.y, { ops: 'risk', code: 'Risk', name: r.title, siteId: r.site.id }),
      ),
    ];
    const live = operations.live;
    // Once the call has created its incident, the incident owns the impact and the panel's eye shows or hides it (#163).
    const owned = live
      ? operations.incidents.some(
          (i) => i.conversationId === live.conversationId && i.reference === live.reference,
        )
      : false;
    const impacts = [
      ...(operations.incidentImpacts ?? []).map((i) => i.impact),
      ...(live?.impact && !owned ? [live.impact] : []),
    ];
    for (const impact of impacts) {
      for (const [route, kind] of [
        [impact.falseRedundancy, 'false'],
        [impact.down, 'down'],
      ] as const) {
        features.push(...route.cables.map((c) => line(c.coordinates, kind)));
        features.push(...route.sites.map((s) => point(s.x, s.y, { ops: `${kind}-site` })));
      }
    }
    features.push(
      ...operations.incidents.map((i) =>
        point(i.site.x, i.site.y, {
          ops: 'incident',
          code: `${i.number} · ${i.priority}`,
          name: i.site.name,
          label: i.priority,
          siteId: i.site.id,
        }),
      ),
    );
    if (live) {
      features.push(
        point(live.site.x, live.site.y, {
          ops: 'fault',
          code: live.site.code,
          name: live.site.name,
          label: live.impact ? `${live.site.name} · ${live.impact.priority}` : live.site.name,
          siteId: live.site.id,
        }),
      );
    }
    source.addFeatures(features);

    // Follow the call: frame the fault and what it takes down, or fly to the station it is about.
    const key = live
      ? `${live.conversationId}|${live.reference}|${live.impact ? 'impact' : 'site'}`
      : '';
    if (live && key !== this.liveKey) {
      const extent = live.impact?.down.extent.length === 4 ? live.impact.down.extent : null;
      if (extent) {
        const [minX, minY, maxX, maxY] = extent;
        this.map!.getView().fit(
          [
            Math.min(minX, live.site.x) - 3_000,
            Math.min(minY, live.site.y) - 3_000,
            Math.max(maxX, live.site.x) + 3_000,
            Math.max(maxY, live.site.y) + 3_000,
          ],
          { padding: [64, 64, 64, 64], duration: 800, maxZoom: 11 },
        );
      } else {
        this.map!.getView().animate({
          center: [live.site.x, live.site.y],
          zoom: 10,
          duration: 800,
        });
      }
    }
    this.liveKey = key;

    // An incident switched on in the panel (#161): frame what it takes down.
    const shown = (operations.incidentImpacts ?? []).map((i) => i.number);
    const added = (operations.incidentImpacts ?? []).find((i) => !this.shownImpacts.has(i.number));
    this.shownImpacts = new Set(shown);
    if (added?.impact.down.extent.length === 4) {
      const [minX, minY, maxX, maxY] = added.impact.down.extent;
      this.map!.getView().fit([minX - 3_000, minY - 3_000, maxX + 3_000, maxY + 3_000], {
        padding: [64, 64, 64, 64],
        duration: 800,
        maxZoom: 11,
      });
    }
  }

  private shownImpacts = new Set<string>();

  private routeStyleCache?: { key: string; line: Style[]; point: Style };

  private routeStyle(feature: FeatureLike): Style | Style[] {
    const palette = (this.palette ??= readPalette(this.host.nativeElement));
    const key = `${palette.focus}|${palette.bg}`;
    if (this.routeStyleCache?.key !== key) {
      this.routeStyleCache = {
        key,
        line: [
          new Style({ stroke: new Stroke({ color: palette.bg, width: 7 }) }),
          new Style({ stroke: new Stroke({ color: palette.focus, width: 3.5 }) }),
        ],
        point: new Style({
          image: new Circle({
            radius: 6,
            fill: new Fill({ color: palette.focus }),
            stroke: new Stroke({ color: palette.bg, width: 2 }),
          }),
          zIndex: 1,
        }),
      };
    }
    return feature.get('route') === 'site' ? this.routeStyleCache.point : this.routeStyleCache.line;
  }

  /** Draws a traced route over a dimmed network and frames it. */
  private showRoute(route: Route | null): void {
    const source = this.route!.getSource()!;
    source.clear(true);
    if (!route) {
      this.network?.setOpacity(this.mapView.highlight()?.points.length ? 0.35 : 1);
      return;
    }
    const features: Feature<Geometry>[] = [
      ...route.cables.map((c) => {
        const f = new Feature<Geometry>(new LineString(c.coordinates.map((p) => [p[0], p[1]])));
        f.setId(c.id);
        f.set('route', 'cable');
        return f;
      }),
      ...route.sites.map((s) => {
        const f = new Feature<Geometry>(new OlPoint([s.x, s.y]));
        f.setId(s.id);
        f.set('route', 'site');
        return f;
      }),
    ];
    source.addFeatures(features);
    this.network?.setOpacity(0.35);
    if (route.extent.length === 4) {
      const [minX, minY, maxX, maxY] = route.extent;
      const pad = Math.max(maxX - minX, maxY - minY) < 2_000 ? 2_000 : 0;
      this.map!.getView().fit([minX - pad, minY - pad, maxX + pad, maxY + pad], {
        padding: [48, 48, 48, 48],
        duration: 400,
        maxZoom: 12,
      });
    }
  }

  /** Draws marked sites and dims the network while there are any, then frames them. */
  private showMarks(
    points: readonly (readonly [number, number, number])[],
    extent: readonly number[] | null,
  ): void {
    const source = this.marks!.getSource()!;
    source.clear(true);
    source.addFeatures(
      points.map(([id, x, y]) => {
        const f = new Feature(new OlPoint([x, y]));
        f.setId(id);
        f.set('mark', true);
        return f;
      }),
    );
    this.network?.setOpacity(points.length > 0 || this.mapView.route() ? 0.35 : 1);
    if (extent && points.length > 0) {
      const [minX, minY, maxX, maxY] = extent;
      const pad = Math.max(maxX - minX, maxY - minY) < 2_000 ? 2_000 : 0;
      this.map!.getView().fit([minX - pad, minY - pad, maxX + pad, maxY + pad], {
        padding: [48, 48, 48, 48],
        duration: 400,
        maxZoom: 11,
      });
    }
  }

  private isSelected(feature: FeatureLike): boolean {
    const top = this.panels.top();
    if (!top) {
      return false;
    }
    const type = feature.get('layer') === 'sites' ? 'site' : 'cable';
    return top.type === type && top.id === String(feature.getId());
  }

  private loadTile(tile: VectorTile<FeatureLike>, url: string): void {
    tile.setLoader((extent, _resolution, projection) => {
      void (async () => {
        try {
          const token = this.auth.accessToken();
          const response = await fetch(url, {
            headers: token ? { Authorization: `Bearer ${token}` } : {},
          });
          if (!response.ok) {
            throw new Error(`${response.status}`);
          }
          const features = tile.getFormat().readFeatures(await response.arrayBuffer(), {
            extent,
            featureProjection: projection,
          });
          tile.setFeatures(features);
        } catch {
          tile.setState(TileState.ERROR);
        }
      })();
    });
  }
}

/** A #rrggbb token with transparency, for fills drawn on the canvas. */
function alpha(hex: string, opacity: number): string {
  return /^#[0-9a-f]{6}$/i.test(hex)
    ? hex +
        Math.round(opacity * 255)
          .toString(16)
          .padStart(2, '0')
    : hex;
}
