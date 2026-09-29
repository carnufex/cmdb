import { httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input } from '@angular/core';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { ImpactListComponent } from './impact-list';
import { apiPath, CableDetail, Impact } from './models';
import { ObjectLinkComponent } from './object-link';

@Component({
  selector: 'cmdb-cable-panel',
  imports: [ObjectLinkComponent, StatusComponent, ImpactListComponent, DecimalPipe],
  template: `
    @if (cable.value(); as c) {
      <header class="header">
        <div class="kind">Kabel · {{ c.medium }}</div>
        <div class="code mono">{{ c.code }}</div>
        <div class="title">{{ c.typeName }}</div>
        <div class="meta">
          <cmdb-status [value]="c.lifecycle" />
          <span>{{ c.lengthM / 1000 | number: '1.1-1' }} km</span>
          <span>{{ c.conductorsInUse }} av {{ c.conductors }} ledare i bruk</span>
        </div>
      </header>
      <section>
        <h3>Ändar</h3>
        <dl class="facts">
          <dt>A</dt>
          <dd><cmdb-link [ref]="c.a" [showName]="true" /></dd>
          <dt>B</dt>
          <dd><cmdb-link [ref]="c.b" [showName]="true" /></dd>
        </dl>
      </section>
      <section>
        <cmdb-impact-list
          [impact]="impact.value()"
          [failed]="!!impact.error()"
          none="Inga tjänster går genom kabeln."
        />
      </section>
      <section>
        <h3>Kretsar genom kabeln ({{ c.circuits.length }})</h3>
        <ul class="links">
          @for (r of c.circuits; track r.id) {
            <li>
              <cmdb-link [ref]="r" /><span class="muted">{{ layers[r.name ?? ''] ?? r.name }}</span>
            </li>
          }
        </ul>
      </section>
    } @else if (cable.error()) {
      <p class="error">Kabeln kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .muted {
      margin: 0;
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CablePanelComponent {
  private readonly panels = inject(PanelStack);

  readonly id = input.required<string>();

  protected readonly cable = httpResource<CableDetail>(() => `/api/${apiPath.cable}/${this.id()}`);
  /** Loaded after the cable itself: impact analysis has its own, larger budget. */
  protected readonly impact = httpResource<Impact>(() =>
    this.cable.value() ? `/api/${apiPath.cable}/${this.id()}/impact` : undefined,
  );
  protected readonly layers: Record<string, string> = {
    physical: 'Fysisk',
    transmission: 'Transmission',
    logical: 'Logisk',
  };

  constructor() {
    effect(() => {
      const c = this.cable.value();
      if (c) {
        this.panels.setLabel({ type: 'cable', id: String(c.id) }, c.code);
      }
    });
  }
}
