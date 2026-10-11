import {
  byPlan,
  changeCategory,
  changeCounts,
  changeRows,
  changeState,
  dependencyChoices,
  nextStep,
  diffPoints,
  PlanOperation,
  PlanSummary,
  planLifecycle,
  planParam,
  resolveSite,
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

  it('finds a site by code among the planned ones first, then by exact code in the search (#107)', async () => {
    const planned = [{ id: -4, code: 'RAD-NY-1', name: 'Ny' }];
    const search = vi.fn(async () => [
      { type: 'equipment', id: 9, code: 'SKP-000001' },
      { type: 'site', id: 12, code: 'SKP-000001' },
      { type: 'site', id: 13, code: 'SKP-0000012' },
    ]);

    expect(await resolveSite('rad-ny-1', planned, search)).toBe(-4);
    expect(search).not.toHaveBeenCalled();
    expect(await resolveSite(' skp-000001 ', planned, search)).toBe(12);
    expect(await resolveSite('SKP-0', planned, search)).toBeNull();
  });
});

describe('the plan workspace (#252)', () => {
  const change = (id: number, kind: string, extra: Partial<PlanOperation> = {}): PlanOperation => ({
    ...op(id, 1),
    kind,
    summary: `${kind} ${id}`,
    ...extra,
  });

  it('sorts every kind of operation into new, changed, removed, moved or connected', () => {
    expect(
      ['create_site', 'create_circuit', 'rename', 'set_attributes', 'remove', 'move'].map(
        changeCategory,
      ),
    ).toEqual(['new', 'new', 'changed', 'changed', 'removed', 'moved']);
    expect(['connect', 'disconnect', 'split_cable', 'link_service'].map(changeCategory)).toEqual([
      'connected',
      'connected',
      'connected',
      'connected',
    ]);
  });

  it('puts what needs attention first, and filters by kind, attention and text', () => {
    const ops = [
      change(1, 'rename', { target: { type: 'site', id: 4, code: 'AGG-0004' } }),
      change(2, 'connect', { conflicts: ['Reserverad av Etapp 3.'] }),
      change(3, 'create_site'),
      change(4, 'disconnect', { problem: 'Terminalerna är inte kopplade.' }),
    ];
    const ids = (q: Parameters<typeof changeRows>[1]) => changeRows(ops, q).map((o) => o.id);
    expect(ids({ filter: null, text: '', sort: 'attention' })).toEqual([4, 2, 1, 3]);
    expect(ids({ filter: null, text: '', sort: 'seq' })).toEqual([1, 2, 3, 4]);
    expect(ids({ filter: 'connected', text: '', sort: 'seq' })).toEqual([2, 4]);
    expect(ids({ filter: 'attention', text: '', sort: 'seq' })).toEqual([2, 4]);
    expect(ids({ filter: null, text: 'agg-0004', sort: 'seq' })).toEqual([1]);
    expect(changeState(ops[3])).toBe('problem');
    expect(changeCounts(ops)).toEqual({
      new: 1,
      changed: 1,
      removed: 0,
      moved: 0,
      connected: 2,
      attention: 2,
    });
  });

  it('names one next step: resolve, add, apply, or back to production', () => {
    expect(nextStep({ ...plan(1), operations: 3 }, 1)).toBe('resolve');
    expect(nextStep({ ...plan(1, 'draft', 'Beroendet avbröts.'), operations: 3 }, 0)).toBe(
      'resolve',
    );
    expect(nextStep({ ...plan(1), operations: 0 }, 0)).toBe('add');
    expect(nextStep({ ...plan(1), operations: 3 }, 0)).toBe('apply');
    expect(nextStep({ ...plan(1, 'applied'), operations: 3 }, 0)).toBe('production');
  });
});
