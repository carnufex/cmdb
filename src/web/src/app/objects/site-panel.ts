import { httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { MapView } from '../map/map-view';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { EditHeaderComponent } from './edit-header';
import { ImpactListComponent } from './impact-list';
import { apiPath, Impact, SiteDetail } from './models';
import { ObjectLinkComponent } from './object-link';

const siteTypes: Record<string, string> = {
  hub: 'Nav',
  aggregation: 'Aggregering',
  radio: 'Radiosite',
  cabinet: 'Skåp',
  splice: 'Skarvpunkt',
};

@Component({
  selector: 'cmdb-site-panel',
  imports: [
    ObjectLinkComponent,
    StatusComponent,
    EditHeaderComponent,
    ImpactListComponent,
    DecimalPipe,
  ],
  template: `
    @if (site.value(); as s) {
      <header class="header">
        <div class="kind">Site · {{ siteTypes[s.siteType] ?? s.siteType }}</div>
        <div class="code mono">{{ s.code }}</div>
        <cmdb-edit-header
          [url]="url()"
          [name]="s.name"
          [lifecycle]="s.lifecycle"
          (saved)="site.reload()"
        />
        <div class="meta">
          <button type="button" class="linkish" (click)="showOnMap(s.x, s.y)">Visa i kartan</button>
          @if (impact.value(); as i) {
            <span>{{ i.services.length }} tjänster och {{ i.circuits }} kretsar berörs</span>
          } @else {
            <span>Beräknar påverkan…</span>
          }
        </div>
      </header>

      @for (location of racks(); track location.id) {
        <section>
          <h3>{{ location.path }}</h3>
          @if (location.equipment.length) {
            <table class="rows">
              <tbody>
                @for (e of location.equipment; track e.id) {
                  <tr>
                    <td>
                      <cmdb-link
                        [ref]="{
                          type: 'equipment',
                          id: e.id,
                          code: e.name,
                          lifecycle: e.lifecycle,
                        }"
                      />
                    </td>
                    <td class="muted">{{ e.model }}</td>
                    <td class="num muted">
                      {{ e.ports }} p{{ e.cards ? ' · ' + e.cards + ' kort' : '' }}
                    </td>
                    <td><cmdb-status [value]="e.lifecycle" /></td>
                  </tr>
                }
              </tbody>
            </table>
          } @else {
            <p class="muted">Tomt.</p>
          }
        </section>
      }

      <section>
        <h3>Kablar ({{ s.cables.length }})</h3>
        @if (s.cables.length) {
          <table class="rows">
            <thead>
              <tr>
                <th>Kabel</th>
                <th>Till</th>
                <th class="num">Längd</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              @for (c of s.cables; track c.id) {
                <tr>
                  <td>
                    <cmdb-link
                      [ref]="{ type: 'cable', id: c.id, code: c.code, lifecycle: c.lifecycle }"
                    />
                    <div class="muted">{{ c.typeName }}</div>
                  </td>
                  <td><cmdb-link [ref]="c.otherEnd" /></td>
                  <td class="num">{{ c.lengthM / 1000 | number: '1.1-1' }} km</td>
                  <td><cmdb-status [value]="c.lifecycle" /></td>
                </tr>
              }
            </tbody>
          </table>
        } @else {
          <p class="muted">Inga kablar.</p>
        }
      </section>

      <section>
        <cmdb-impact-list
          [impact]="impact.value()"
          [failed]="!!impact.error()"
          none="Inga tjänster går genom siten."
        />
      </section>
    } @else if (site.error()) {
      <p class="error">Siten kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .linkish {
      padding: 0;
      border: 0;
      background: none;
      color: var(--text);
      text-decoration: underline dotted;
      cursor: pointer;
    }
    .muted {
      margin: 0;
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SitePanelComponent {
  private readonly panels = inject(PanelStack);
  private readonly mapView = inject(MapView);

  readonly id = input.required<string>();

  protected readonly siteTypes = siteTypes;
  protected readonly url = computed(() => `/api/${apiPath.site}/${this.id()}`);
  protected readonly site = httpResource<SiteDetail>(() => this.url());
  /** Loaded after the site itself: impact analysis has its own, larger budget. */
  protected readonly impact = httpResource<Impact>(() =>
    this.site.value() ? `${this.url()}/impact` : undefined,
  );

  /** Only locations that hold equipment, titled with their full path (Byggnad A / Nodrum / Rack 1). */
  protected readonly racks = computed(() => {
    const s = this.site.value();
    if (!s) {
      return [];
    }
    const byId = new Map(s.locations.map((l) => [l.id, l]));
    const path = (id: number | null): string => {
      const l = id === null ? undefined : byId.get(id);
      return l ? (l.parentId ? `${path(l.parentId)} / ${l.name}` : l.name) : '';
    };
    return s.locations.filter((l) => l.equipment.length).map((l) => ({ ...l, path: path(l.id) }));
  });

  constructor() {
    effect(() => {
      const s = this.site.value();
      if (s) {
        this.panels.setLabel({ type: 'site', id: String(s.id) }, s.code);
      }
    });
  }

  protected showOnMap(x: number, y: number): void {
    this.mapView.focus({ x, y });
  }
}
