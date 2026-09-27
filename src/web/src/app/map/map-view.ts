import { Injectable, signal } from '@angular/core';

/** A point in SWEREF 99 TM. */
export interface Point {
  x: number;
  y: number;
}

/**
 * What the map is looking at, shared with other parts of the app: search ranks hits near the centre, and
 * anything can ask the map to go somewhere.
 */
@Injectable({ providedIn: 'root' })
export class MapView {
  readonly center = signal<Point | null>(null);
  readonly focusRequest = signal<(Point & { seq: number }) | null>(null);

  private seq = 0;

  focus(point: Point): void {
    this.focusRequest.set({ ...point, seq: ++this.seq });
  }
}
