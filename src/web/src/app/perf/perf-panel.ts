import { ChangeDetectionStrategy, Component, inject, OnDestroy, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { Tools } from '../shell/tools';
import { Measurement } from './perf-model';
import { PerfRunner, Progress } from './perf-runner';
import { ToolSizeComponent } from '../shell/tool-size';

/**
 * The performance panel (#56): one click runs the interactions in the budget against the loaded network and
 * shows p50 and p95 for the server and for the browser. Status is a dot and text, judged on the server's p95.
 */
@Component({
  selector: 'cmdb-perf-panel',
  imports: [ToolSizeComponent, DecimalPipe],
  templateUrl: './perf-panel.html',
  styleUrl: './perf-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PerfPanelComponent implements OnDestroy {
  private readonly runner = inject(PerfRunner);
  protected readonly tools = inject(Tools);

  protected readonly results = signal<Measurement[]>([]);
  protected readonly progress = signal<Progress | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly finishedAt = signal<Date | null>(null);

  private abort?: AbortController;

  protected readonly statusLabels = {
    ok: 'Inom budget',
    over: 'Över budget',
    reference: 'Referens',
  } as const;

  async run(): Promise<void> {
    this.abort = new AbortController();
    this.error.set(null);
    this.results.set([]);
    this.finishedAt.set(null);
    try {
      const results = await this.runner.run((p) => this.progress.set(p), this.abort.signal);
      this.results.set(results);
      this.finishedAt.set(new Date());
    } catch (e: unknown) {
      this.error.set(
        this.abort.signal.aborted ? 'Mätningen avbröts.' : `Mätningen misslyckades: ${String(e)}`,
      );
    } finally {
      this.progress.set(null);
    }
  }

  protected cancel(): void {
    this.abort?.abort();
  }

  protected close(): void {
    this.cancel();
    this.tools.close();
  }

  ngOnDestroy(): void {
    this.cancel();
  }
}
