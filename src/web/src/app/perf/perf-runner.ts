import { inject, Injectable } from '@angular/core';
import { Auth } from '../auth/auth';
import { GRID, HOME_EXTENT } from '../map/map-grid';
import { MapView } from '../map/map-view';
import { examples, QueryFields, toRequest } from '../query/query-model';
import { Measurement, parseServerTiming, Sample, summarize } from './perf-model';

export interface Progress {
  label: string;
  done: number;
  total: number;
}

interface Hit {
  type: string;
  id: number;
  code: string;
  name: string | null;
}

const RUNS = 30;
const WARMUP = 3;

/**
 * Runs the interactions in the performance budget (docs/plan.md) against the network that is loaded, with
 * inputs drawn from that network. Requests run one at a time so each measures latency, not throughput, and
 * a few warm-up requests per operation are thrown away (JIT and connection pool after a restart).
 */
@Injectable({ providedIn: 'root' })
export class PerfRunner {
  private readonly auth = inject(Auth);
  private readonly mapView = inject(MapView);

  async run(onProgress: (p: Progress) => void, signal: AbortSignal): Promise<Measurement[]> {
    const results: Measurement[] = [];

    onProgress({ label: 'Hämtar stickprov ur nätet', done: 0, total: 1 });
    const fields = (await this.request<QueryFields>('/api/query/fields', signal)).body;
    const hits = (
      await Promise.all(
        ['RAD-00', 'SKP-00', 'AGG-0', 'HUB-0', 'K-00', 'TJ-00'].map(
          async (q) =>
            (await this.request<Hit[]>(`/api/search?q=${encodeURIComponent(q)}&limit=50`, signal))
              .body,
        ),
      )
    ).flat();
    const sites = hits.filter((h) => h.type === 'site');
    const cables = hits.filter((h) => h.type === 'cable');
    const services = hits.filter((h) => h.type === 'service');

    // Everyday terms, codes, a numeric id and terms without hits: the last ones expose bad query plans.
    const terms = [
      ...pick(sites, 10).map((s) => s.code),
      ...pick(sites, 6).map((s) => s.code.slice(0, 6)),
      ...pick(sites, 4).map((s) => s.name ?? s.code),
      'SKP',
      'HUB',
      'Radiosite 12',
      'K-000',
      'xyzzy',
      'nod',
      '42',
      String(pick(sites, 1)[0]?.id ?? 1),
      'ODF',
      'BB-6',
    ];

    results.push(
      await this.measure('search', 'Snabbsök', 50, RUNS, onProgress, signal, (i) =>
        this.request(
          `/api/search?q=${encodeURIComponent(terms[i % terms.length])}&limit=20`,
          signal,
        ),
      ),
    );
    results.push(
      await this.measure('open', 'Öppna objekt (site)', 50, RUNS, onProgress, signal, (i) =>
        this.request(`/api/sites/${sites[i % sites.length].id}`, signal),
      ),
    );
    results.push(
      await this.measure('impact', 'Påverkansanalys, kabel', 200, RUNS, onProgress, signal, (i) =>
        this.request(`/api/cables/${cables[i % cables.length].id}/impact`, signal),
      ),
    );
    results.push(
      await this.measure('trace', 'Spåra tjänst', 50, RUNS, onProgress, signal, (i) =>
        this.request(`/api/trace?service=${services[i % services.length].id}`, signal),
      ),
    );
    const tiles = randomTiles(RUNS + WARMUP);
    results.push(
      await this.measure('tiles', 'Kartplatta', 100, RUNS, onProgress, signal, (i) =>
        this.request(`/api/tiles/${tiles[i].join('/')}`, signal, 'binary'),
      ),
    );
    results.push(
      // Each example is its own query shape; warm every one up once so p95 is not one cold plan.
      await this.measure(
        'query',
        'Avancerad sökning',
        null,
        2 * examples.length,
        onProgress,
        signal,
        (i) =>
          this.request(
            '/api/query/sites',
            signal,
            'json',
            toRequest(examples[i % examples.length].draft, fields, 50),
          ),
        examples.length,
      ),
    );

    const bench = this.mapView.renderBenchmark;
    if (bench) {
      onProgress({ label: 'Kartrendering: panorering och zoom', done: 0, total: 1 });
      const frames = await bench(signal);
      results.push(
        summarize(
          'render',
          'Kartrendering (bildtid)',
          frames.map((f) => ({ server: null, browser: f })),
          33,
          'mål ≥ 30 fps, ej i budgeten',
        ),
      );
    }
    return results;
  }

  private async measure(
    key: string,
    label: string,
    budget: number | null,
    runs: number,
    onProgress: (p: Progress) => void,
    signal: AbortSignal,
    call: (i: number) => Promise<{ sample: Sample }>,
    warmup = WARMUP,
  ): Promise<Measurement> {
    const samples: Sample[] = [];
    for (let i = 0; i < runs + warmup; i++) {
      signal.throwIfAborted();
      onProgress({ label, done: Math.max(0, i - warmup), total: runs });
      const { sample } = await call(i);
      if (i >= warmup) {
        samples.push(sample);
      }
    }
    return summarize(key, label, samples, budget);
  }

  private async request<T>(
    url: string,
    signal: AbortSignal,
    kind: 'json' | 'binary' = 'json',
    body?: unknown,
  ): Promise<{ sample: Sample; body: T }> {
    const token = this.auth.accessToken();
    const headers: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    const started = performance.now();
    const response = await fetch(url, {
      method: body === undefined ? 'GET' : 'POST',
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: 'no-store',
      signal,
    });
    const payload = kind === 'json' ? await response.json() : await response.arrayBuffer();
    const browser = performance.now() - started;
    if (!response.ok) {
      throw new Error(`${url} svarade ${response.status}`);
    }
    return {
      sample: { browser, server: parseServerTiming(response.headers.get('server-timing')) },
      body: payload as T,
    };
  }
}

function pick<T>(items: readonly T[], n: number): T[] {
  return Array.from(
    { length: Math.min(n, items.length) },
    () => items[Math.floor(Math.random() * items.length)],
  );
}

/** Tiles over Sweden at zoom 0–8, in the API's tile grid (map-grid.ts, Tiles.cs). */
export function randomTiles(n: number): [number, number, number][] {
  const [minX, minY, maxX, maxY] = HOME_EXTENT;
  return Array.from({ length: n }, () => {
    const z = Math.floor(Math.random() * 9);
    const span = (GRID.maxX - GRID.minX) / 2 ** z;
    const x0 = Math.floor((minX - GRID.minX) / span);
    const x1 = Math.floor((maxX - GRID.minX) / span);
    const y0 = Math.floor((GRID.maxY - maxY) / span);
    const y1 = Math.floor((GRID.maxY - minY) / span);
    const x = x0 + Math.floor(Math.random() * (x1 - x0 + 1));
    const y = y0 + Math.floor(Math.random() * (y1 - y0 + 1));
    return [z, x, y];
  });
}
