import { Injectable } from '@angular/core';

/** A rendering benchmark of a front panel drawn as its picture: how many ports, and the frame times in ms. */
export interface FrontPanelBenchmark {
  model: string;
  ports: number;
  frames: number[];
}

/** What the performance panel (#56) reaches in a front panel drawn as its picture (#214) while one is shown. */
@Injectable({ providedIn: 'root' })
export class FrontPanelView {
  /**
   * Set by the front panel while it shows a picture: marks a moving run of ports every frame, as Shift-click does,
   * and returns the frame times.
   */
  renderBenchmark: ((signal: AbortSignal) => Promise<FrontPanelBenchmark>) | null = null;
}
