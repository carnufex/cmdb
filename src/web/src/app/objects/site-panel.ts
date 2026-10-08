import { PlanRemoveComponent } from '../plans/plan-remove';
import { ClassificationComponent, DerivedClassification } from './classification';
import { RackViewComponent } from './rack-view';
import { httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { MapView } from '../map/map-view';
import { ActivePlan } from '../plans/active-plan';
import { CatalogKinds } from '../shell/catalog-kinds';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { EditHeaderComponent } from './edit-header';
import { ImpactListComponent } from './impact-list';
import { apiPath, Impact, SiteDetail } from './models';
import { ObjectLinkComponent } from './object-link';

@Component({
  selector: 'cmdb-site-panel',
  imports: [
    ClassificationComponent,
    PlanRemoveComponent,
    RackViewComponent,
    ObjectLinkComponent,
    StatusComponent,
    EditHeaderComponent,
    ImpactListComponent,
    DecimalPipe,
  ],
  template: `
    @if (site.value(); as s) {
      <header class="header">
        <div class="kind">Site · {{ kinds.siteTypeName(s.siteType) }}</div>
        <div class="code mono">{{ s.code }}</div>
        <cmdb-edit-header
          [url]="url()"
          [name]="s.name"
          [lifecycle]="s.lifecycle"
          (saved)="site.reload()"
        />
        <div class="meta">
          @if (s.x !== null && s.y !== null) {
            <button type="button" class="linkish" (click)="showOnMap(s.x, s.y)">
              Visa i kartan
            </button>
          }
          @if (impact.value(); as i) {
            <span>{{ i.services.length }} tjänster och {{ i.circuits }} kretsar berörs</span>
          } @else {
            <span>Beräknar påverkan…</span>
          }
        </div>
      </header>
      <cmdb-classification type="site" [objectId]="s.id" />
      <cmdb-plan-remove type="site" [objectId]="s.id" />
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

      @for (location of racks(); track location.id) {
        <section>
          <h3>
            {{ location.path }}
            @if (location.derivedLevel) {
              <span
                class="status"
                [attr.data-critical]="location.derivedLevel >= 5"
                [title]="'Härledd av ' + location.because"
              >
                <span class="dot" aria-hidden="true"></span>{{ location.derivedLevel }}
              </span>
            }
          </h3>
          @if (location.rackUnits) {
            <cmdb-rack-view [units]="location.rackUnits" [equipment]="location.equipment" />
          }
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
    h3 .status {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
      margin-left: var(--space-2);
      font-size: var(--text-xs);
      font-weight: 600;
    }
    h3 .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--status-in-service);
    }
    h3 [data-critical='true'] .dot {
      background: var(--status-conflict);
    }
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
  private readonly plan = inject(ActivePlan);

  readonly id = input.required<string>();

  protected readonly kinds = inject(CatalogKinds);
  /** The site's attributes, named from its type's schema (#211). */
  protected readonly attributes = computed(() => {
    const s = this.site.value();
    return s ? this.kinds.attributes('site', s.siteType, s.attributes) : [];
  });
  protected readonly url = computed(() => `/api/${apiPath.site}/${this.id()}`);
  protected readonly site = httpResource<SiteDetail>(() => this.url());
  /** Loaded after the site itself: impact analysis has its own, larger budget. */
  protected readonly impact = httpResource<Impact>(() => {
    return this.site.value()
      ? this.plan.request(`${this.url()}/impact${this.plan.param(true)}`)
      : undefined;
  });

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
    // The level a rack gets from the equipment in it (#177), from the derived classification of the site.
    const derived = new Map((this.derived.value()?.locationLevels ?? []).map((d) => [d.id, d]));
    return s.locations
      .filter((l) => l.equipment.length)
      .map((l) => ({
        ...l,
        path: path(l.id),
        derivedLevel: derived.get(l.id)?.level ?? null,
        because: derived.get(l.id)?.because ?? '',
      }));
  });

  private readonly derived = httpResource<DerivedClassification>(() => {
    return this.plan.request(
      `/api/classifications/derived?type=site&id=${this.id()}${this.plan.param()}`,
    );
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
