import { Injectable, signal } from '@angular/core';

/** Tools opened from the toolbar. They sit over the left edge of the lens, never on top of the panel stack. */
export type Tool = 'query' | 'perf' | 'tree' | 'plans' | 'grid' | 'changelog';

@Injectable({ providedIn: 'root' })
export class Tools {
  private readonly current = signal<Tool | null>(null);

  readonly open = this.current.asReadonly();

  toggle(tool: Tool): void {
    this.current.update((t) => (t === tool ? null : tool));
  }

  close(): void {
    this.current.set(null);
  }
}
