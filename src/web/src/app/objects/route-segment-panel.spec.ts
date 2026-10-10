import { crossSection } from './route-segment-panel';

describe('crossSection', () => {
  it('puts a single tube in the middle', () => {
    const [tube] = crossSection(1);
    expect([tube.x, tube.y]).toEqual([60, 60]);
  });

  it('places every tube inside the duct without overlap', () => {
    for (const count of [7, 24]) {
      const tubes = crossSection(count);
      expect(tubes.length).toBe(count);
      for (const t of tubes) {
        expect(Math.hypot(t.x - 60, t.y - 60) + t.r).toBeLessThanOrEqual(57);
      }
      for (let i = 0; i < tubes.length; i++) {
        for (let j = i + 1; j < tubes.length; j++) {
          const gap = Math.hypot(tubes[i].x - tubes[j].x, tubes[i].y - tubes[j].y);
          expect(gap).toBeGreaterThanOrEqual(tubes[i].r + tubes[j].r - 0.01);
        }
      }
    }
  });
});
