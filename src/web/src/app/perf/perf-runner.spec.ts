import { GRID, HOME_EXTENT } from '../map/map-grid';
import { randomTiles } from './perf-runner';

describe('randomTiles', () => {
  it('stays over Sweden in the API tile grid', () => {
    const [minX, minY, maxX, maxY] = HOME_EXTENT;
    for (const [z, x, y] of randomTiles(500)) {
      const span = (GRID.maxX - GRID.minX) / 2 ** z;
      const left = GRID.minX + x * span;
      const top = GRID.maxY - y * span;
      expect(z).toBeGreaterThanOrEqual(0);
      expect(z).toBeLessThanOrEqual(8);
      // The tile overlaps the home extent.
      expect(left).toBeLessThanOrEqual(maxX);
      expect(left + span).toBeGreaterThanOrEqual(minX);
      expect(top).toBeGreaterThanOrEqual(minY);
      expect(top - span).toBeLessThanOrEqual(maxY);
    }
  });
});
