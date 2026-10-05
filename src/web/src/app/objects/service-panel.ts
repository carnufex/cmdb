import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { apiPath, ServiceDetail } from './models';
import { ClassificationComponent } from './classification';
import { ObjectLinkComponent } from './object-link';
import { traceId } from './trace-model';

@Component({
  selector: 'cmdb-service-panel',
  imports: [ClassificationComponent, ObjectLinkComponent, StatusComponent],
  template: `
    @if (service.value(); as s) {
      <header class="header">
        <div class="kind">Tjänst · {{ s.serviceType }}</div>
        <div class="code mono">{{ s.code }}</div>
        <div class="title">{{ s.name }}</div>
        <div class="meta">
          <cmdb-status [value]="s.lifecycle" />
          <button type="button" class="action" (click)="trace(s.id)">Spåra ände till ände</button>
        </div>
      </header>
      <cmdb-classification type="service" [objectId]="s.id" />
      <section>
        <h3>Bärs av</h3>
        @for (c of s.circuits; track c.circuit.id) {
          <dl class="facts">
            <dt>Krets</dt>
            <dd><cmdb-link [ref]="c.circuit" /></dd>
            @if (c.a) {
              <dt>A-ände</dt>
              <dd>
                {{ c.a.label }} på <cmdb-link [ref]="c.a.owner" /> (<cmdb-link [ref]="c.a.site" />)
              </dd>
            }
            @if (c.b) {
              <dt>B-ände</dt>
              <dd>
                {{ c.b.label }} på <cmdb-link [ref]="c.b.owner" /> (<cmdb-link [ref]="c.b.site" />)
              </dd>
            }
          </dl>
        }
      </section>
      @if (attributes().length) {
        <section>
          <h3>Attribut</h3>
          <dl class="facts">
            @for (a of attributes(); track a[0]) {
              <dt>{{ a[0] }}</dt>
              <dd class="mono">{{ a[1] }}</dd>
            }
          </dl>
        </section>
      }
    } @else if (service.error()) {
      <p class="error">Tjänsten kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ServicePanelComponent {
  private readonly panels = inject(PanelStack);

  readonly id = input.required<string>();

  protected readonly service = httpResource<ServiceDetail>(
    () => `/api/${apiPath.service}/${this.id()}`,
  );
  protected readonly attributes = computed(() =>
    Object.entries(this.service.value()?.attributes ?? {}).map(([k, v]) => [k, String(v)] as const),
  );

  protected trace(id: number): void {
    this.panels.open({ type: 'trace', id: traceId({ by: 'service', id }) });
  }

  constructor() {
    effect(() => {
      const s = this.service.value();
      if (s) {
        this.panels.setLabel({ type: 'service', id: String(s.id) }, s.code);
      }
    });
  }
}
