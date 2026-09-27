/** One timed operation: server time from Server-Timing (null when absent) and time seen by the browser. */
export interface Sample {
  server: number | null;
  browser: number;
}

export interface Percentiles {
  p50: number;
  p95: number;
}

export type Status = 'ok' | 'over' | 'reference';

export interface Measurement {
  key: string;
  label: string;
  /** Budget in ms (p95) from docs/plan.md, or a target for the rendering test; null means reference only. */
  budget: number | null;
  budgetNote?: string;
  n: number;
  server: Percentiles | null;
  browser: Percentiles;
  status: Status;
}

/** Nearest-rank percentile. Empty input gives NaN. */
export function percentile(values: readonly number[], q: number): number {
  if (values.length === 0) {
    return NaN;
  }
  const sorted = [...values].sort((a, b) => a - b);
  const rank = Math.ceil(q * sorted.length);
  return sorted[Math.min(sorted.length, Math.max(1, rank)) - 1];
}

function percentiles(values: readonly number[]): Percentiles {
  return { p50: percentile(values, 0.5), p95: percentile(values, 0.95) };
}

/**
 * Summarises samples. The budget is judged on the server's p95 when the server reported times, since the
 * browser time also contains the network (in the demo, a Cloudflare tunnel); otherwise on the browser's p95.
 */
export function summarize(
  key: string,
  label: string,
  samples: readonly Sample[],
  budget: number | null,
  budgetNote?: string,
): Measurement {
  const serverTimes = samples.map((s) => s.server).filter((s): s is number => s !== null);
  const server =
    serverTimes.length === samples.length && samples.length > 0 ? percentiles(serverTimes) : null;
  const browser = percentiles(samples.map((s) => s.browser));
  const judged = (server ?? browser).p95;
  const status: Status = budget === null ? 'reference' : judged <= budget ? 'ok' : 'over';
  return { key, label, budget, budgetNote, n: samples.length, server, browser, status };
}

export function parseServerTiming(header: string | null): number | null {
  const match = header?.match(/(?:^|,)\s*app;dur=([\d.]+)/);
  return match ? Number(match[1]) : null;
}
