import { GRID, networkTileGrid, resolutions, tileUrl } from './map-grid';

describe('map grid', () => {
  it('matches the API grid: one z0 tile over the bounds, halving per level', () => {
    expect(resolutions[0]).toBeCloseTo((GRID.maxX - GRID.minX) / 256);
    expect(resolutions[5]).toBeCloseTo(resolutions[0] / 32);
    expect(resolutions).toHaveLength(GRID.maxZoom + 1);
  });

  it('numbers tile rows from the top like ST_TileEnvelope', () => {
    const grid = networkTileGrid();
    // A point in the north-west quarter is tile (1, 0, 0); the south-west quarter is (1, 0, 1).
    expect(grid.getTileCoordForCoordAndZ([GRID.minX + 10, GRID.maxY - 10], 1)).toEqual([1, 0, 0]);
    expect(grid.getTileCoordForCoordAndZ([GRID.minX + 10, GRID.minY + 10], 1)).toEqual([1, 0, 1]);
    expect(tileUrl(1, 0, 1)).toBe('/api/tiles/1/0/1');
  });
});
