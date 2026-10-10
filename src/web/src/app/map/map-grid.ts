import { register } from 'ol/proj/proj4';
import { get as getProjection } from 'ol/proj';
import TileGrid from 'ol/tilegrid/TileGrid';
import proj4 from 'proj4';

/** SWEREF 99 TM, the only projection the map uses (ADR-0004). */
export const SWEREF = 'EPSG:3006';

/** Must match TileGrid in src/api/Features/Map/Tiles.cs: one square z0 tile over these bounds. */
export const GRID = {
  minX: -1_200_000,
  maxX: 1_800_000,
  minY: 5_500_000,
  maxY: 8_500_000,
  maxZoom: 16,
  detailZoom: 5,
} as const;

export const TILE_SIZE = 256;

export const resolutions = Array.from(
  { length: GRID.maxZoom + 1 },
  (_, z) => (GRID.maxX - GRID.minX) / TILE_SIZE / 2 ** z,
);

/** Roughly Sweden, used for the initial view and to keep panning sensible. */
export const HOME_EXTENT = [240_000, 6_100_000, 950_000, 7_720_000];

let registered = false;

export function registerSweref(): void {
  if (registered) {
    return;
  }
  proj4.defs(
    SWEREF,
    '+proj=utm +zone=33 +ellps=GRS80 +towgs84=0,0,0,0,0,0,0 +units=m +no_defs +type=crs',
  );
  register(proj4);
  getProjection(SWEREF)!.setExtent([GRID.minX, GRID.minY, GRID.maxX, GRID.maxY]);
  registered = true;
}

export function networkTileGrid(): TileGrid {
  return new TileGrid({
    extent: [GRID.minX, GRID.minY, GRID.maxX, GRID.maxY],
    origin: [GRID.minX, GRID.maxY],
    resolutions,
    tileSize: TILE_SIZE,
  });
}

/** OpenLayers counts tile rows downwards from the top-left origin, like ST_TileEnvelope. */
export function tileUrl(z: number, x: number, y: number): string {
  return `/api/tiles/${z}/${x}/${y}`;
}

/** The conduit's own tiles (#236): route segments, apart from the network's. */
export function conduitTileUrl(z: number, x: number, y: number): string {
  return `/api/tiles/conduit/${z}/${x}/${y}`;
}
