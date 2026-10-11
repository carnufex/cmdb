import { runState, valueText } from './reconciliation-model';

describe('reconciliation model', () => {
  it('names the most pressing state of a run', () => {
    const run = { dryRun: false, reviewPlanId: 3, errors: 0 };
    expect(runState(run)).toBe('review');
    expect(runState({ ...run, reviewPlanId: null })).toBe('done');
    expect(runState({ ...run, dryRun: true })).toBe('dry');
    expect(runState({ ...run, autoApplyProblem: 'konflikt' })).toBe('problem');
    expect(runState({ ...run, errors: ['sites.csv:2 fel'] })).toBe('failed');
  });

  it('shows values as short text', () => {
    expect(valueText('SN-1')).toBe('SN-1');
    expect(valueText([1, 2])).toBe('[1,2]');
    expect(valueText(null)).toBe('–');
  });
});
