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
}

/** One operation in words, with what it touches and, when it no longer fits production, why. */
export interface PlanOperation {
  id: number;
  planId: number;
  seq: number;
  kind: 'connect' | 'disconnect' | 'set_lifecycle' | 'rename';
  summary: string;
  terminals: TraceHop[];
  target: ObjectRef | null;
  connectionKind: string | null;
  lifecycle: string | null;
  name: string | null;
  problem: string | null;
  createdBy: string;
  createdAt: string;
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
  | { kind: 'rename'; type: 'site' | 'equipment'; objectId: number; name: string };

export const planStatusLabels: Record<PlanSummary['status'], string> = {
  draft: 'Utkast',
  applied: 'Införd',
  cancelled: 'Avbruten',
};

/** The status dot for a plan: drafts are planned, applied plans are in service, flags are conflicts. */
export function planLifecycle(plan: Pick<PlanSummary, 'status' | 'flag'>): Lifecycle {
  if (plan.flag) {
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
  diff: Pick<PlanDiff, 'plans' | 'changes'>,
): { plan: PlanSummary; changes: PlanOperation[] }[] {
  return diff.plans.map((plan) => ({
    plan,
    changes: diff.changes.filter((c) => c.planId === plan.id),
  }));
}
