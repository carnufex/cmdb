import { clusterLabel, clusterPoints } from './map-clusters';

describe('clusterPoints', () => {
  it('keeps a site alone as itself, with its id', () => {
    expect(clusterPoints([[7, 100, 200]], 1000)).toEqual([
      { x: 100, y: 200, count: 1, id: 7, extent: [100, 200, 100, 200] },
    ]);
  });

  it('gathers sites in a cell at their centre, and says what they cover', () => {
    const [cluster] = clusterPoints(
      [
        [1, 100, 200],
        [2, 300, 400],
      ],
      1000,
    );
    expect(cluster).toEqual({
      x: 200,
      y: 300,
      count: 2,
      id: undefined,
      extent: [100, 200, 300, 400],
    });
  });

  it('counts every site, also 50 000 of them, and fewer clusters the further out', () => {
    const points = Array.from(
      { length: 50_000 },
      (_, i) =>
        [i, 400_000 + ((i * 7919) % 500_000), 6_200_000 + ((i * 104_729) % 1_400_000)] as const,
    );
    const out = clusterPoints(points, 40_000);
    const near = clusterPoints(points, 2_000);
    expect(out.reduce((sum, c) => sum + c.count, 0)).toBe(50_000);
    expect(near.reduce((sum, c) => sum + c.count, 0)).toBe(50_000);
    expect(out.length).toBeLessThan(near.length);
  });

  it('does not mix up cells across the axes', () => {
    expect(
      clusterPoints(
        [
          [1, 1000, 5000],
          [2, 5000, 1000],
        ],
        1000,
      ).length,
    ).toBe(2);
  });
});

describe('clusterLabel', () => {
  it('stays short', () => {
    expect(clusterLabel(12)).toBe('12');
    expect(clusterLabel(1234)).toBe('1,2k');
    expect(clusterLabel(28_866)).toBe('29k');
  });
});
