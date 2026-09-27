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
import { MapView } from './map-view';

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
  private palette?: Palette;

  constructor() {
    afterNextRender(() => this.create());
    inject(DestroyRef).onDestroy(() => this.map?.setTarget(undefined));

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
    const layers: (TileLayer | VectorTileLayer | VectorLayer)[] = [this.network, this.marks];
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
    const pending = this.mapView.highlight();
    if (pending) {
      this.showMarks(pending.points, pending.extent);
    }
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
        feature && !feature.get('mark')
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
      if (feature?.get('mark')) {
        this.panels.open({ type: 'site', id: String(feature.getId()) }, { replace: true });
      } else if (feature) {
        const type = feature.get('layer') === 'sites' ? 'site' : 'cable';
        this.panels.open({ type, id: String(feature.getId()) }, { replace: true });
      }
    });
  }

  private restyle: () => void = () => undefined;

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
    this.network?.setOpacity(points.length > 0 ? 0.35 : 1);
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
