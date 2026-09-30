import { Injectable } from '@angular/core';

/** A rendering benchmark of the neighbourhood graph: how many sites were drawn, and the frame times in ms. */
export interface GraphBenchmark {
  nodes: number;
  frames: number[];
}

/** What the performance panel (#56, #89) reaches in the neighbourhood graph while the lens is shown. */
@Injectable({ providedIn: 'root' })
export class GraphView {
  /**
   * Set by the graph lens while it is shown: focuses the given site, expands until at least `nodes` sites are
   * drawn (or nothing is left to expand), animates the camera and returns the frame times.
   */
  renderBenchmark:
    ((start: number, nodes: number, signal: AbortSignal) => Promise<GraphBenchmark>) | null = null;
}
