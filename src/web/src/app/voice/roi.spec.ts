import { EXAMPLE, roi } from './roi';

describe('ROI model', () => {
  it('adds the four sources of value and pays back in months', () => {
    const r = roi({ ...EXAMPLE, solutionCostPerYear: 1_200_000 });
    expect(r.intake).toBe((6000 * 6 * 700) / 60);
    expect(r.mttr).toBe((40 * 20 * 50000) / 60);
    expect(r.dispatches).toBe(60 * 8000);
    expect(r.prevented).toBe(400000);
    expect(r.total).toBe(r.intake + r.mttr + r.dispatches + r.prevented);
    expect(r.net).toBe(r.total - 1_200_000);
    expect(r.paybackMonths).toBeCloseTo((1_200_000 / r.total) * 12);
  });

  it('follows every assumption and has no payback without value', () => {
    const zero = roi({
      ...EXAMPLE,
      callsPerYear: 0,
      criticalOutagesPerYear: 0,
      dispatchesAvoidedPerYear: 0,
      preventedOutagesPerYear: 0,
    });
    expect(zero.total).toBe(0);
    expect(zero.paybackMonths).toBeNull();
    expect(roi({ ...EXAMPLE, callsPerYear: 12000 }).intake).toBe(2 * roi(EXAMPLE).intake);
  });
});
