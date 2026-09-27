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
import TileState from 'ol/TileState';
import VectorTile from 'ol/VectorTile';
import VectorTileSource from 'ol/source/VectorTile';
import WMTS from 'ol/source/WMTS';
import WMTSTileGrid from 'ol/tilegrid/WMTS';
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
import { createStyler, Palette, readPalette } from './map-style';

interface Hover {
  x: number;
  y: number;
  code: string;
  name: string | null;
}

/**
 * The map lens. OpenLayers is kept inside this component so the engine can be swapped (ADR-0004).
 * Tiles come from the API with the user's token; nothing is fetched from foreign map services.
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
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly target = viewChild.required<ElementRef<HTMLDivElement>>('target');

  protected readonly hover = signal<Hover | null>(null);
  protected readonly zoom = signal(0);
  protected readonly detailZoom = GRID.detailZoom;

  private map?: OlMap;
  private network?: VectorTileLayer;
  private palette?: Palette;

  constructor() {
    afterNextRender(() => this.create());
    inject(DestroyRef).onDestroy(() => this.map?.setTarget(undefined));

    // Re-read the palette when the theme changes, and redraw the selection when the panel stack changes.
    effect(() => {
      this.theme.theme();
      this.palette = undefined;
      this.restyle();
    });
    effect(() => {
      this.panels.top();
      this.network?.changed();
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
      renderMode: 'hybrid',
    });

    const layers: (TileLayer | VectorTileLayer)[] = [this.network];
    if (this.config.lantmaterietKey) {
      layers.unshift(this.background(this.config.lantmaterietKey));
    }

    this.map = new OlMap({
      target: this.target().nativeElement,
      layers,
      controls: [],
      view: new View({
        projection: SWEREF,
        resolutions,
        constrainResolution: true,
        extent: [GRID.minX, GRID.minY, GRID.maxX, GRID.maxY],
      }),
    });
    this.map.getView().fit(HOME_EXTENT, { padding: [24, 24, 24, 24] });
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
        feature
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
      if (feature) {
        const type = feature.get('layer') === 'sites' ? 'site' : 'cable';
        this.panels.open({ type, id: String(feature.getId()) }, { replace: true });
      }
    });
  }

  private restyle: () => void = () => undefined;

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

  /** Lantmäteriet's toned-down topographic web map (CC BY), in SWEREF 99 TM. Needs an API key. */
  private background(key: string): TileLayer {
    const lmResolutions = Array.from({ length: 16 }, (_, z) => 4096 / 2 ** z);
    return new TileLayer({
      opacity: 0.55,
      source: new WMTS({
        url: `https://api.lantmateriet.se/open/topowebb-ccby/v1/wmts/token/${key}/`,
        layer: 'topowebb_nedtonad',
        matrixSet: '3006',
        format: 'image/png',
        style: 'default',
        projection: SWEREF,
        attributions: '© Lantmäteriet (CC BY 4.0)',
        tileGrid: new WMTSTileGrid({
          origin: [-1_200_000, 8_500_000],
          resolutions: lmResolutions,
          matrixIds: lmResolutions.map((_, z) => String(z)),
        }),
      }),
    });
  }
}
