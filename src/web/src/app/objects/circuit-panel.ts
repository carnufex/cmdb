import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input } from '@angular/core';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { apiPath, CircuitDetail } from './models';
import { ObjectLinkComponent } from './object-link';
import { SourcesComponent } from './sources';
import { traceId } from './trace-model';

@Component({
  selector: 'cmdb-circuit-panel',
  imports: [SourcesComponent, ObjectLinkComponent, StatusComponent],
  template: `
    @if (circuit.value(); as c) {
      <header class="header">
        <div class="kind">Krets · {{ layers[c.layer] ?? c.layer }}</div>
        <div class="code mono">{{ c.code }}</div>
        <div class="meta">
          <cmdb-status [value]="c.lifecycle" /><span>{{ c.hops.length }} hopp</span>
          <button type="button" class="action" (click)="trace(c.id)">Spåra</button>
        </div>
      </header>
      @if (c.services.length) {
        <section>
          <h3>Tjänster</h3>
          <ul class="links">
            @for (s of c.services; track s.id) {
              <li><cmdb-link [ref]="s" [showName]="true" /></li>
            }
          </ul>
        </section>
      }
      @if (c.carriers.length || c.carried.length) {
        <section>
          <h3>Lager</h3>
          <dl class="facts">
            @for (r of c.carriers; track r.id) {
              <dt>Bärs av</dt>
              <dd>
                <cmdb-link [ref]="r" /> <span class="muted">{{ layers[r.name ?? ''] }}</span>
              </dd>
            }
            @for (r of c.carried; track r.id) {
              <dt>Bär</dt>
              <dd>
                <cmdb-link [ref]="r" /> <span class="muted">{{ layers[r.name ?? ''] }}</span>
              </dd>
            }
          </dl>
        </section>
      }
      <section>
        <h3>Väg</h3>
        <ol class="path">
          @for (h of c.hops; track h.seq) {
            <li>
              <span class="label">{{ h.terminal.label }}</span>
              <cmdb-link [ref]="h.terminal.owner" />
              <span class="muted">@ <cmdb-link [ref]="h.terminal.site" /></span>
              @if (h.channel) {
                <span class="channel mono">{{ h.channel }}</span>
              }
            </li>
          }
        </ol>
      </section>
      <cmdb-sources [sources]="c.sources" />
    } @else if (circuit.error()) {
      <p class="error">Kretsen kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .path {
      margin: 0;
      padding: 0 0 0 var(--space-4);
      font-size: var(--text-sm);
      li {
        display: flex;
        flex-wrap: wrap;
        gap: var(--space-1) var(--space-2);
        padding: 3px 0;
        border-left: 1px solid var(--border-strong);
        margin-left: -12px;
        padding-left: 11px;
      }
      .label {
        color: var(--text-muted);
      }
    }
    .channel {
      font-size: var(--text-xs);
      color: var(--status-construction);
      border: 1px solid currentColor;
      border-radius: var(--radius-sm);
      padding: 0 4px;
    }
    .muted {
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CircuitPanelComponent {
  private readonly panels = inject(PanelStack);

  readonly id = input.required<string>();

  protected readonly circuit = httpResource<CircuitDetail>(
    () => `/api/${apiPath.circuit}/${this.id()}`,
  );
  protected readonly layers: Record<string, string> = {
    physical: 'Fysisk',
    transmission: 'Transmission',
    logical: 'Logisk',
  };

  protected trace(id: number): void {
    this.panels.open({ type: 'trace', id: traceId({ by: 'circuit', id }) });
  }

  constructor() {
    effect(() => {
      const c = this.circuit.value();
      if (c) {
        this.panels.setLabel({ type: 'circuit', id: String(c.id) }, c.code);
      }
    });
  }
}
