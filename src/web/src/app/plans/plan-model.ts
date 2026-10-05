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
}

/** One operation in words, with what it touches and, when it no longer fits production, why. */
export interface PlanOperation {
  id: number;
  planId: number;
  seq: number;
  kind:
    | 'connect'
    | 'disconnect'
    | 'set_lifecycle'
    | 'rename'
    | 'create_site'
    | 'create_equipment'
    | 'create_cable';
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
