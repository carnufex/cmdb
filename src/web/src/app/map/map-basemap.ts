import TileLayer from 'ol/layer/Tile';
import XYZ from 'ol/source/XYZ';
import { Theme } from '../shell/theme';

/** Which background the map draws under the network. `none` makes no requests at all (isolated environments). */
export type Basemap = 'esri' | 'none';

const ESRI = 'https://services.arcgisonline.com/ArcGIS/rest/services/Canvas';

export const ESRI_ATTRIBUTION = 'Kartbakgrund © Esri, HERE, Garmin, © OpenStreetMap-bidragsgivare';

/** Unknown or empty settings fall back to Esri, the POC default (ADR-0010). */
export function parseBasemap(value: string | undefined): Basemap {
  return value === 'none' ? 'none' : 'esri';
}

/**
 * Esri's keyless Canvas basemaps, toned to match the theme. They are Web Mercator tiles; OpenLayers
 * reprojects them into SWEREF 99 TM on the fly.
 */
export function esriTileUrl(theme: Theme): string {
  const service = theme === 'dark' ? 'World_Dark_Gray_Base' : 'World_Light_Gray_Base';
  return `${ESRI}/${service}/MapServer/tile/{z}/{y}/{x}`;
}

export function createBasemap(theme: Theme): TileLayer<XYZ> {
  return new TileLayer({
    source: new XYZ({
      url: esriTileUrl(theme),
      projection: 'EPSG:3857',
      crossOrigin: 'anonymous',
      maxZoom: 16,
      attributions: ESRI_ATTRIBUTION,
      attributionsCollapsible: false,
    }),
  });
}
