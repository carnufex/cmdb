import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MapView } from '../map/map-view';
import { IconComponent } from '../shell/icons';
import { Lifecycle, StatusComponent } from '../shell/status';
import { ObjectRef } from './models';
import { ObjectLinkComponent } from './object-link';

/** GET /api/service-points/{id} (#292) */
export interface ServicePointDetail {
  id: number;
  code: string;
  name: string | null;
  address: string | null;
  typeKey: string;
  typeName: string;
  units: number;
  status: ServicePointStatus;
  x: number | null;
  y: number | null;
  site: ObjectRef | null;
  port: { terminalId: number; equipment: ObjectRef; name: string } | null;
  sourceSystem: string | null;
  externalId: string | null;
  attributes: string;
  updatedAt: string;
}

export type ServicePointStatus = 'planned' | 'ordered' | 'connected';

/** A service point's status as the status dot shows it: planned, ordered like something being built, connected like in service. */
export function servicePointLifecycle(status: string): Lifecycle {
  return status === 'connected'
    ? 'in_service'
    : status === 'ordered'
      ? 'under_construction'
      : 'planned';
}

export function servicePointStatusText(status: string): string {
  return status === 'connected' ? 'Ansluten' : status === 'ordered' ? 'Beställd' : 'Planerad';
}

/**
 * A service point (#292, ADR-0021): an address or customer the network is to reach. Its type, the homes or premises
 * behind it, its status as dot and text, and the site and port that serve it once connected.
 */
@Component({
  selector: 'cmdb-service-point-panel',
  imports: [IconComponent, StatusComponent, ObjectLinkComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (point.hasValue()) {
      @let p = point.value();
      <header class="header">
        <div class="kind">
          <cmdb-icon name="service-point" />Anslutningspunkt · {{ p.typeName }}
        </div>
        <div class="code mono">{{ p.code }}</div>
        @if (p.address || p.name) {
          <div class="name">{{ p.address ?? p.name }}</div>
        }
        <div class="meta">
          <cmdb-status [value]="lifecycle(p.status)" [text]="statusText(p.status)" />
          <span>{{ p.units }} {{ p.units === 1 ? 'enhet' : 'enheter' }}</span>
          @if (p.x !== null && p.y !== null) {
            <button type="button" class="linkish" (click)="showInMap(p.x, p.y)">
              Visa i kartan
            </button>
          }
        </div>
      </header>
      <section>
        <h3>Anslutning</h3>
        @if (p.site; as site) {
          <dl class="facts">
            <dt>Site</dt>
            <dd><cmdb-link [ref]="site" [showName]="true" /></dd>
            @if (p.port; as port) {
              <dt>Port</dt>
              <dd><cmdb-link [ref]="port.equipment" /> · {{ port.name }}</dd>
            }
          </dl>
        } @else {
          <p class="muted">Inte ansluten än. Koppla den till en site i en plan.</p>
        }
      </section>
      @if (p.sourceSystem || p.externalId || p.name) {
        <section>
          <h3>Uppgifter</h3>
          <dl class="facts">
            @if (p.name) {
              <dt>Namn</dt>
              <dd>{{ p.name }}</dd>
            }
            @if (p.sourceSystem) {
              <dt>Källa</dt>
              <dd>{{ p.sourceSystem }}</dd>
            }
            @if (p.externalId) {
              <dt>Externt id</dt>
              <dd class="mono">{{ p.externalId }}</dd>
            }
          </dl>
        </section>
      }
    } @else if (point.error()) {
      <p class="error">Anslutningspunkten kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
})
export class ServicePointPanelComponent {
  readonly id = input.required<string>();
  private readonly mapView = inject(MapView);

  protected readonly point = httpResource<ServicePointDetail>(
    () => `/api/service-points/${this.id()}`,
  );
  protected readonly code = computed(() => (this.point.hasValue() ? this.point.value().code : ''));
  protected readonly lifecycle = servicePointLifecycle;
  protected readonly statusText = servicePointStatusText;

  protected showInMap(x: number, y: number): void {
    this.mapView.focus({ x, y });
  }
}
