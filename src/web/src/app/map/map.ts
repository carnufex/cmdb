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
import { Geometry } from 'ol/geom';
import { Circle, Fill, Stroke, Style } from 'ol/style';
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
import { MapView, Route } from './map-view';

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
    const layers: (TileLayer | VectorTileLayer | VectorLayer)[] = [
      this.network,
      this.marks,
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
              name: feature.get('name') ?? null,
            }
          : null,
      );
    });
    this.map.on('click', (e) => {
      const feature = this.map!.forEachFeatureAtPixel(e.pixel, (f) => f, { hitTolerance: 4 });
      if (feature?.get('route')) {
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
