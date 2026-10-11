import { ObjectRef } from '../objects/models';
import { TraceHop } from '../objects/trace-model';
import { Lifecycle } from '../shell/status';

/** GET /api/plans: a plan (#24, ADR-0005). */
export interface PlanSummary {
  id: number;
  name: string;
  description: string;
  status: 'draft' | 'applied' | 'cancelled';
  /** Why the plan needs attention: a dependency was cancelled, or applying one made operations no longer fit. */
  flag: string | null;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
  appliedBy: string | null;
  appliedAt: string | null;
  dependsOn: number[];
  operations: number;
  /** Operations whose resources others have reserved or want too (#25). */
  conflicts: number;
  /** api (web and REST) or mcp: proposed by an agent (#64), to be reviewed and applied by a person. */
  createdVia: 'api' | 'mcp';
  client: string | null;
  /** Why the plan was applied although it made requirements of a classification level unmet (#179). */
  exception?: string | null;
}

/** One operation in words, with what it touches and, when it no longer fits production, why. */
export interface PlanOperation {
  id: number;
  planId: number;
  seq: number;
  /** connect, rename, create_site, remove, move … (see {@link changeCategory}). */
  kind: string;
  summary: string;
  terminals: TraceHop[];
  target: ObjectRef | null;
  connectionKind: string | null;
  lifecycle: string | null;
  name: string | null;
  problem: string | null;
  createdBy: string;
  createdAt: string;
  /** Others' claims on the same resources (#25). */
  conflicts: string[];
  /** Someone else's reservation stops the plan from being applied. */
  blocked: boolean;
}

/** GET /api/plans/{id} */
export interface PlanDetail {
  plan: PlanSummary;
  dependencies: PlanSummary[];
  operations: PlanOperation[];
}

/** GET /api/plans/{id}/view: the plan's view as a diff against production. */
export interface PlanDiff {
  plan: PlanSummary;
  /** The draft plans in the view, dependencies first, the plan itself last. */
  plans: PlanSummary[];
  changes: PlanOperation[];
  sites: { id: number; x: number; y: number }[];
  extent: number[] | null;
  problems: number;
  elapsedMs: number;
  /** Operations per plan; a big plan lists only some of them in `changes` (#170). */
  counts?: Record<number, number> | null;
  /** What the plan creates (#107), for the map. */
  planned: {
    sites: { id: number; code: string; name: string; siteType: string; x: number; y: number }[];
    cables: { id: number; code: string; coordinates: number[][] }[];
    /** What the plan removes, and cables a split replaces (#168, #172). */
    removed?: {
      sites: { id: number; x: number; y: number }[];
      cables: { id: number; code: string; coordinates: number[][] }[];
    } | null;
  } | null;
}

/** POST /api/plans/{id}/apply and /cancel */
export interface ApplyResult {
  plan: PlanSummary;
  flagged: PlanSummary[];
}

/** An operation to add (POST /api/plans/{id}/operations). */
export type NewOperation =
  | {
      kind: 'connect';
      a: number;
      b: number;
      connectionKind: 'patch' | 'splice' | 'termination' | 'internal';
    }
  | { kind: 'disconnect'; a: number; b: number }
  | {
      kind: 'set_lifecycle';
      type: 'site' | 'equipment' | 'cable';
      objectId: number;
      lifecycle: string;
    }
  | { kind: 'rename'; type: 'site' | 'equipment'; objectId: number; name: string }
  | { kind: 'create_site'; code: string; name: string; siteType: string; x: number; y: number }
  | {
      kind: 'create_equipment';
      siteId: number;
      typeKey: string;
      name: string;
      rack?: string;
      room?: string;
      position?: number;
    }
  | { kind: 'create_cable'; aSiteId: number; bSiteId: number; typeKey: string }
  | { kind: 'split_cable'; cableId: number; siteId: number; terminate: number[] }
  | { kind: 'remove'; type: 'site' | 'equipment' | 'cable'; objectId: number }
  | {
      kind: 'move';
      type: 'equipment';
      objectId: number;
      siteId: number;
      rack?: string;
      position?: number;
    }
  | { kind: 'move'; type: 'cable'; objectId: number; siteId: number; end: 'A' | 'B' }
  | {
      kind: 'set_conductor_usage';
      type: 'cable';
      objectId: number;
      conductors: number[];
      usage: 'dark' | 'dark_fibre' | 'spare' | null;
    }
  | {
      kind: 'set_classification';
      type: 'site' | 'equipment' | 'cable' | 'service';
      objectId: number;
      schema: string;
      level: number | null;
    }
  | {
      kind: 'set_attributes';
      type: 'site' | 'equipment';
      objectId: number;
      attributes: Record<string, string | number | null>;
    };

export const planStatusLabels: Record<PlanSummary['status'], string> = {
  draft: 'Utkast',
  applied: 'Införd',
  cancelled: 'Avbruten',
};

/** The status dot for a plan: drafts are planned, applied plans are in service, flags are conflicts. */
export function planLifecycle(
  plan: Pick<PlanSummary, 'status' | 'flag'> & { conflicts?: number },
): Lifecycle {
  if (plan.flag || (plan.status === 'draft' && (plan.conflicts ?? 0) > 0)) {
    return 'conflict';
  }
  return plan.status === 'draft' ? 'planned' : plan.status === 'applied' ? 'in_service' : 'removed';
}

/** The sites a plan touches, as map marks: [id, x, y]. */
export function diffPoints(diff: Pick<PlanDiff, 'sites'>): [number, number, number][] {
  return diff.sites.map((s) => [s.id, s.x, s.y]);
}

/** A query string parameter for requests that can look at a plan instead of production. */
export function planParam(planId: number | null, first = false): string {
  return planId === null ? '' : `${first ? '?' : '&'}plan=${planId}`;
}

/** Plans a new plan could build on: drafts and applied plans, never cancelled ones. */
export function dependencyChoices(plans: readonly PlanSummary[]): PlanSummary[] {
  return plans.filter((p) => p.status !== 'cancelled');
}

/** Operations grouped by the plan they belong to, in the order the view applies them. */
export function byPlan(
  diff: Pick<PlanDiff, 'plans' | 'changes'> & { counts?: Record<number, number> | null },
): { plan: PlanSummary; changes: PlanOperation[]; total: number }[] {
  return diff.plans.map((plan) => {
    const changes = diff.changes.filter((c) => c.planId === plan.id);
    return { plan, changes, total: Math.max(diff.counts?.[plan.id] ?? 0, changes.length) };
  });
}

/** A site the user can pick by code: planned in the plan (negative id) or found in production. */
export interface SiteChoice {
  id: number;
  code: string;
  name: string;
}

/**
 * The site with this code (#107): planned in the plan first, otherwise an exact code match among the quick search's
 * hits; null when there is none.
 */
export async function resolveSite(
  code: string,
  planned: readonly SiteChoice[],
  search: (q: string) => Promise<{ type: string; id: number; code: string }[]>,
): Promise<number | null> {
  const wanted = code.trim().toUpperCase();
  const own = planned.find((s) => s.code.toUpperCase() === wanted);
  if (own) {
    return own.id;
  }
  const hits = await search(code.trim());
  return hits.find((h) => h.type === 'site' && h.code.toUpperCase() === wanted)?.id ?? null;
}

/** What kind of change an operation is, for filtering the plan's changes (#252). */
export type ChangeCategory = 'new' | 'changed' | 'removed' | 'moved' | 'connected';

export const changeCategories: readonly ChangeCategory[] = [
  'new',
  'changed',
  'removed',
  'moved',
  'connected',
];

export const changeCategoryLabels: Record<ChangeCategory, string> = {
  new: 'Ny',
  changed: 'Ändrad',
  removed: 'Borttagen',
  moved: 'Flyttad',
  connected: 'Kopplad',
};

export function changeCategory(kind: string): ChangeCategory {
  if (kind.startsWith('create_')) {
    return 'new';
  }
  switch (kind) {
    case 'remove':
      return 'removed';
    case 'move':
      return 'moved';
    case 'connect':
    case 'disconnect':
    case 'split_cable':
    case 'link_circuit':
    case 'link_service':
      return 'connected';
    default:
      return 'changed';
  }
}

/**
 * Where an operation stands: it no longer fits production (problem), someone else's reservation stops the plan
 * (blocked), others claim the same resources (conflict), or it is fine.
 */
export type ChangeState = 'problem' | 'blocked' | 'conflict' | 'ok';

export function changeState(
  op: Pick<PlanOperation, 'problem' | 'blocked' | 'conflicts'>,
): ChangeState {
  return op.problem ? 'problem' : op.blocked ? 'blocked' : op.conflicts.length ? 'conflict' : 'ok';
}

export const changeStateLabels: Record<ChangeState, string> = {
  problem: 'Passar inte längre',
  blocked: 'Stoppas av reservation',
  conflict: 'Konflikt',
  ok: 'Klar',
};

/** Changes needing attention first, then in the order the plan applies them. */
const stateOrder: Record<ChangeState, number> = { problem: 0, blocked: 1, conflict: 2, ok: 3 };

export interface ChangeQuery {
  /** A kind of change, or those needing attention; null for all. */
  filter: ChangeCategory | 'attention' | null;
  text: string;
  sort: 'attention' | 'seq' | 'category' | 'object';
}

/** The plan's changes as the table shows them: filtered by kind and text, and sorted. */
export function changeRows(ops: readonly PlanOperation[], query: ChangeQuery): PlanOperation[] {
  const words = query.text.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const rows = ops.filter((op) => {
    if (
      query.filter === 'attention'
        ? changeState(op) === 'ok'
        : query.filter && changeCategory(op.kind) !== query.filter
    ) {
      return false;
    }
    const text = `${op.summary} ${op.target?.code ?? ''} ${op.target?.name ?? ''}`.toLowerCase();
    return words.every((w) => text.includes(w));
  });
  const code = (op: PlanOperation) => op.target?.code ?? '';
  const by: Record<ChangeQuery['sort'], (a: PlanOperation, b: PlanOperation) => number> = {
    attention: (a, b) => stateOrder[changeState(a)] - stateOrder[changeState(b)] || a.seq - b.seq,
    seq: (a, b) => a.seq - b.seq,
    category: (a, b) =>
      changeCategories.indexOf(changeCategory(a.kind)) -
        changeCategories.indexOf(changeCategory(b.kind)) || a.seq - b.seq,
    object: (a, b) => code(a).localeCompare(code(b), 'sv') || a.seq - b.seq,
  };
  return rows.sort(by[query.sort]);
}

/** How many changes of each kind, and how many need attention. */
export function changeCounts(
  ops: readonly PlanOperation[],
): Record<ChangeCategory, number> & { attention: number } {
  const counts = { new: 0, changed: 0, removed: 0, moved: 0, connected: 0, attention: 0 };
  for (const op of ops) {
    counts[changeCategory(op.kind)]++;
    if (changeState(op) !== 'ok') {
      counts.attention++;
    }
  }
  return counts;
}

/** The one thing to do next with a plan (#252). */
export type NextStep = 'resolve' | 'add' | 'apply' | 'production';

export function nextStep(
  plan: Pick<PlanSummary, 'status' | 'flag' | 'operations'>,
  attention: number,
): NextStep {
  if (plan.status !== 'draft') {
    return 'production';
  }
  if (attention > 0 || plan.flag) {
    return 'resolve';
  }
  return plan.operations === 0 ? 'add' : 'apply';
}

export const nextStepLabels: Record<NextStep, string> = {
  resolve: 'Lös konflikter',
  add: 'Lägg till ändringar',
  apply: 'För in i produktion',
  production: 'Visa produktion',
};

/** GET /api/plans/{id}/impact (#252) */
export type PathState = 'intact' | 'broken' | 'absent' | 'degraded';

export interface PlanImpact {
  circuits: {
    circuit: ObjectRef;
    layer: string;
    before: PathState;
    after: PathState;
    services: number;
  }[];
  services: { service: ObjectRef; before: PathState; after: PathState }[];
  broken: number;
  hiddenCircuits: number;
  hiddenServices: number;
  truncated: boolean;
  elapsedMs: number;
}

export const pathStateLabels: Record<PathState, string> = {
  intact: 'Hel',
  broken: 'Bruten',
  absent: 'Finns inte',
  degraded: 'Försvagad',
};

/** The status dot for a path state. */
export function pathLifecycle(state: PathState): Lifecycle {
  return state === 'intact'
    ? 'in_service'
    : state === 'broken'
      ? 'conflict'
      : state === 'degraded'
        ? 'under_construction'
        : 'removed';
}
