/** GET /api/reconciliations (#216, #217) */
export interface ReconciliationSummary {
  id: number;
  source: string;
  runBy: string;
  startedAt: string;
  dryRun: boolean;
  elapsedMs: number;
  reviewPlanId: number | null;
  appliedPlanId: number | null;
  errors: number;
}

export interface ReconciliationCount {
  objectType: string;
  reported: number;
  matched: number;
  linked: number;
  new: number;
  changed: number;
  unchanged: number;
  deviations: number;
  missing: number;
  outsideScope: number;
}

export interface ReconciliationDeviation {
  objectType: string;
  objectId: number | null;
  externalId: string;
  attribute: string;
  source: unknown;
  cmdb: unknown;
  reason: string;
}

/** GET /api/reconciliations/{id} */
export interface ReconciliationReport {
  id: number;
  source: string;
  dryRun: boolean;
  counts: ReconciliationCount[];
  operations: number;
  reasons: Record<string, number>;
  deviations: ReconciliationDeviation[];
  errors: string[];
  notReconciled: string[];
  reviewPlanId: number | null;
  appliedPlanId: number | null;
  autoApplyProblem: string | null;
  elapsedMs: number;
}

export type RunState = 'failed' | 'problem' | 'dry' | 'review' | 'done';

export const runStateLabels: Record<RunState, string> = {
  failed: 'Fel i filerna',
  problem: 'Betrodd plan stoppad',
  dry: 'Provkörning',
  review: 'Väntar på granskning',
  done: 'Klar',
};

/** One state per run, the most pressing first; shown as a dot and text. */
export function runState(r: {
  dryRun: boolean;
  reviewPlanId: number | null;
  errors: number | string[];
  autoApplyProblem?: string | null;
}): RunState {
  const errors = typeof r.errors === 'number' ? r.errors : r.errors.length;
  if (errors > 0) {
    return 'failed';
  }
  if (r.autoApplyProblem) {
    return 'problem';
  }
  if (r.dryRun) {
    return 'dry';
  }
  return r.reviewPlanId !== null ? 'review' : 'done';
}

export const objectTypeLabels: Record<string, string> = {
  site: 'Site',
  equipment: 'Utrustning',
  cable: 'Kabel',
  service: 'Tjänst',
};

export const reasonLabels: Record<string, string> = {
  'owned-by-other': 'en annan källa äger attributet',
  'not-allowed': 'källan får inte ändra attributet',
  'no-operation': 'ingen planoperation ändrar det ännu',
  ambiguous: 'flera objekt matchar',
  missing: 'rapporteras inte längre',
  'code-taken': 'koden används redan',
  'outside-scope': 'utanför omfånget',
  'unknown-site': 'okänd site',
  'cannot-create': 'kan inte skapas ännu',
};

/** A reported value as short text: strings as is, the rest as JSON, nothing as a dash. */
export function valueText(value: unknown): string {
  if (value === null || value === undefined) {
    return '–';
  }
  return typeof value === 'string' ? value : JSON.stringify(value);
}
