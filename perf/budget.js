// The performance budget (docs/plan.md) as a k6 run (#13): every row that exists today, against a loaded network,
// with inputs drawn from that network. The budget is judged on the server's p95 (Server-Timing), as in the
// app's performance panel; the client time is reported too.
//
// Run through scripts/perf.sh. Environment: CMDB_URL, CMDB_TOKEN, RATE (requests/s, below the agent rate limit),
// DURATION.
import http from 'k6/http';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';

const BASE = __ENV.CMDB_URL;
const TOKEN = __ENV.CMDB_TOKEN;
const RATE = Number(__ENV.RATE || 15);
const DURATION = __ENV.DURATION || '60s';

/** Budget rows in plan order; null budget means not measurable yet. */
export const ROWS = [
  { key: 'search', label: 'Snabbsök', budget: 50 },
  { key: 'open', label: 'Öppna objekt med närmaste grannar', budget: 50 },
  { key: 'trace', label: 'Spåra tjänst ände till ände', budget: 50 },
  { key: 'impact', label: 'Påverkansanalys för en kabelsträcka', budget: 200 },
  { key: 'tiles', label: 'Kartplatta per omfång', budget: 100 },
  { key: 'plan', label: 'Växla vy mellan produktion och plan', budget: 100, pending: 'finns inte ännu (#24)' },
];
const MEASURED = ROWS.filter((r) => !r.pending);

const server = Object.fromEntries(MEASURED.map((r) => [r.key, new Trend(`server_${r.key}`, true)]));
const client = Object.fromEntries(MEASURED.map((r) => [r.key, new Trend(`client_${r.key}`, true)]));
const failures = new Counter('failures');

export const options = {
  scenarios: {
    budget: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 4,
      maxVUs: 16,
    },
  },
  thresholds: {
    ...Object.fromEntries(MEASURED.map((r) => [`server_${r.key}`, [`p(95)<${r.budget}`]])),
    failures: ['count==0'],
  },
  summaryTrendStats: ['p(50)', 'p(95)', 'max', 'count'],
};

const headers = { Authorization: `Bearer ${TOKEN}` };

function get(path, op) {
  return http.get(BASE + path, { headers, tags: { op }, responseType: op === 'tiles' ? 'binary' : 'text' });
}

export function setup() {
  const hits = ['RAD-00', 'SKP-00', 'AGG-0', 'HUB-0', 'K-00', 'TJ-00'].flatMap((q) => {
    const res = get(`/api/search?q=${encodeURIComponent(q)}&limit=50`, 'setup');
    if (res.status !== 200) {
      throw new Error(`search ${q}: ${res.status} ${res.body}`);
    }
    return res.json();
  });
  const sites = hits.filter((h) => h.type === 'site');
  const cables = hits.filter((h) => h.type === 'cable');
  const services = hits.filter((h) => h.type === 'service');
  if (!sites.length || !cables.length || !services.length) {
    throw new Error('The network has no sites, cables or services to sample.');
  }
  // Everyday terms, codes, a numeric id and terms without hits: the last ones expose bad query plans.
  const terms = [
    ...sites.slice(0, 10).map((s) => s.code),
    ...sites.slice(10, 16).map((s) => s.code.slice(0, 6)),
    ...sites.slice(16, 20).map((s) => s.name ?? s.code),
    'SKP', 'HUB', 'Radiosite 12', 'K-000', 'xyzzy', 'nod', '42', String(sites[0].id), 'ODF', 'BB-6',
  ];
  return { sites, cables, services, terms };
}

// Tiles over Sweden at zoom 0–8 in the API's grid (map-grid.ts, Tiles.cs).
const GRID = { minX: -1_200_000, maxX: 1_800_000, maxY: 8_500_000 };
const HOME = [240_000, 6_100_000, 950_000, 7_720_000];

function randomTile() {
  const z = Math.floor(Math.random() * 9);
  const span = (GRID.maxX - GRID.minX) / 2 ** z;
  const x0 = Math.floor((HOME[0] - GRID.minX) / span);
  const x1 = Math.floor((HOME[2] - GRID.minX) / span);
  const y0 = Math.floor((GRID.maxY - HOME[3]) / span);
  const y1 = Math.floor((GRID.maxY - HOME[1]) / span);
  return `${z}/${x0 + Math.floor(Math.random() * (x1 - x0 + 1))}/${y0 + Math.floor(Math.random() * (y1 - y0 + 1))}`;
}

function pick(items, i) {
  return items[i % items.length];
}

export default function (data) {
  const i = exec.scenario.iterationInTest;
  const row = MEASURED[i % MEASURED.length];
  const n = Math.floor(i / MEASURED.length);
  const path = {
    search: () => `/api/search?q=${encodeURIComponent(pick(data.terms, n))}&limit=20`,
    open: () => `/api/sites/${pick(data.sites, n).id}`,
    trace: () => `/api/trace?service=${pick(data.services, n).id}`,
    impact: () => `/api/cables/${pick(data.cables, n).id}/impact`,
    tiles: () => `/api/tiles/${randomTile()}`,
  }[row.key]();

  const res = get(path, row.key);
  if (res.status !== 200) {
    failures.add(1, { op: row.key });
    console.error(`${path}: ${res.status}`);
    return;
  }
  client[row.key].add(res.timings.duration);
  const timing = /dur=([\d.]+)/.exec(res.headers['Server-Timing'] || '');
  if (timing) {
    server[row.key].add(Number(timing[1]));
  }
}

function value(data, metric, stat) {
  const v = data.metrics[metric]?.values?.[stat];
  return v === undefined ? null : v;
}

function ms(v) {
  return v === null ? '–' : v.toFixed(1);
}

export function handleSummary(data) {
  const when = new Date().toISOString();
  const rows = ROWS.map((r) => {
    if (r.pending) {
      return { ...r, status: 'pending' };
    }
    const p95 = value(data, `server_${r.key}`, 'p(95)');
    return {
      ...r,
      n: value(data, `server_${r.key}`, 'count') ?? 0,
      server: { p50: value(data, `server_${r.key}`, 'p(50)'), p95, max: value(data, `server_${r.key}`, 'max') },
      client: { p50: value(data, `client_${r.key}`, 'p(50)'), p95: value(data, `client_${r.key}`, 'p(95)') },
      status: p95 === null ? 'no-data' : p95 < r.budget ? 'ok' : 'over',
    };
  });
  const failed = value(data, 'failures', 'count') ?? 0;
  const regression = rows.some((r) => r.status === 'over' || r.status === 'no-data') || failed > 0;
  const mark = { ok: '✅ inom budget', over: '❌ **över budget**', 'no-data': '❌ inga mätvärden', pending: '⏳ ' };
  const md = [
    `## Prestandabudget – ${regression ? '❌ regression' : '✅ inom budget'}`,
    '',
    `Mål: \`${BASE}\` · ${when} · ${RATE} anrop/s i ${DURATION} · server = \`Server-Timing\`, klient inkluderar nätverket.`,
    '',
    '| Interaktion | Budget (p95) | Server p50 / p95 / max | Klient p50 / p95 | n | Status |',
    '|---|---|---|---|---|---|',
    ...rows.map((r) =>
      r.status === 'pending'
        ? `| ${r.label} | < ${r.budget} ms | – | – | – | ${mark.pending}${r.pending} |`
        : `| ${r.label} | < ${r.budget} ms | ${ms(r.server.p50)} / ${ms(r.server.p95)} / ${ms(r.server.max)} | ${ms(r.client.p50)} / ${ms(r.client.p95)} | ${r.n} | ${mark[r.status]} |`,
    ),
    '',
    failed > 0 ? `**${failed} anrop misslyckades.**` : 'Inga misslyckade anrop.',
    '',
  ].join('\n');
  const json = JSON.stringify({ target: BASE, at: when, rate: RATE, duration: DURATION, regression, failed, rows }, null, 2);
  return { stdout: md + '\n', '/out/summary.md': md, '/out/summary.json': json };
}
