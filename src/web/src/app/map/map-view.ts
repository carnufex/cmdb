import { Injectable, signal } from '@angular/core';

/** A point in SWEREF 99 TM. */
export interface Point {
  x: number;
  y: number;
}

/** A set of sites to mark in the map, e.g. advanced search results: [id, x, y] per site. */
export interface Highlight {
  points: readonly (readonly [number, number, number])[];
  extent: readonly number[] | null;
  seq: number;
}

/**
 * What the map is looking at, shared with other parts of the app: search ranks hits near the centre, and
 * anything can ask the map to go somewhere or mark a set of sites.
 */
@Injectable({ providedIn: 'root' })
export class MapView {
  readonly center = signal<Point | null>(null);
  readonly focusRequest = signal<(Point & { seq: number }) | null>(null);
  readonly highlight = signal<Highlight | null>(null);

  /**
   * Set by the map while it is shown: animates the view over the country and returns frame times in ms.
   * Used by the performance panel (#56).
   */
  renderBenchmark: ((signal: AbortSignal) => Promise<number[]>) | null = null;

  private seq = 0;

  focus(point: Point): void {
    this.focusRequest.set({ ...point, seq: ++this.seq });
  }

  mark(points: Highlight['points'], extent: readonly number[] | null): void {
    this.highlight.set({ points, extent, seq: ++this.seq });
  }

  clearMarks(): void {
    this.highlight.set(null);
  }
}
