import {
  byPlan,
  dependencyChoices,
  diffPoints,
  PlanOperation,
  PlanSummary,
  planLifecycle,
  planParam,
} from './plan-model';

const plan = (
  id: number,
  status: PlanSummary['status'] = 'draft',
  flag: string | null = null,
): PlanSummary => ({
  id,
  name: `Plan ${id}`,
  description: '',
  status,
  flag,
  createdBy: 'test',
  createdAt: '',
  updatedAt: '',
  appliedBy: null,
  appliedAt: null,
  dependsOn: [],
  operations: 0,
  conflicts: 0,
  createdVia: 'api',
  client: null,
});

const op = (id: number, planId: number): PlanOperation => ({
  id,
  planId,
  seq: id,
  kind: 'connect',
  summary: `op ${id}`,
  terminals: [],
  target: null,
  connectionKind: 'patch',
  lifecycle: null,
  name: null,
  problem: null,
  createdBy: 'test',
  createdAt: '',
  conflicts: [],
  blocked: false,
});

describe('plan model', () => {
  it('shows drafts as planned, applied as in service, cancelled as removed and flags as conflicts', () => {
    expect(planLifecycle(plan(1))).toBe('planned');
    expect(planLifecycle(plan(1, 'applied'))).toBe('in_service');
    expect(planLifecycle(plan(1, 'cancelled'))).toBe('removed');
    expect(planLifecycle(plan(1, 'draft', 'Beroendet avbröts.'))).toBe('conflict');
    expect(planLifecycle({ ...plan(1), conflicts: 2 })).toBe('conflict');
    expect(planLifecycle({ ...plan(1, 'applied'), conflicts: 2 })).toBe('in_service');
  });

  it('adds the plan to requests only when one is active', () => {
    expect(planParam(null)).toBe('');
    expect(planParam(7)).toBe('&plan=7');
    expect(planParam(7, true)).toBe('?plan=7');
  });

  it('marks the touched sites in the map', () => {
    expect(diffPoints({ sites: [{ id: 3, x: 500000, y: 6600000 }] })).toEqual([
      [3, 500000, 6600000],
    ]);
  });

  it('offers drafts and applied plans to build on, never cancelled ones', () => {
    expect(
      dependencyChoices([plan(1), plan(2, 'applied'), plan(3, 'cancelled')]).map((p) => p.id),
    ).toEqual([1, 2]);
  });

  it('groups a view by plan in the order the view applies them', () => {
    const groups = byPlan({
      plans: [plan(2), plan(5)],
      changes: [op(10, 2), op(11, 5), op(12, 2)],
    });
    expect(groups.map((g) => [g.plan.id, g.changes.map((c) => c.id)])).toEqual([
      [2, [10, 12]],
      [5, [11]],
    ]);
  });
});
