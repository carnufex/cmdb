import { httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusComponent } from '../shell/status';
import { ImpactListComponent } from './impact-list';
import { asLifecycle, Impact, ObjectRef } from './models';
import { ObjectLinkComponent } from './object-link';

/** GET /api/route-segments/{id} (#236) */
export interface RouteSegmentDetail {
  id: number;
  code: string;
  construction: string;
  owner: string | null;
  lifecycle: string;
  lengthM: number;
  a: ObjectRef;
  b: ObjectRef;
  freeSubducts: number;
  ducts: {
    id: number;
    code: string;
    typeKey: string;
    typeName: string;
    outerDiameterMm: number;
    innerDiameterMm: number;
    lifecycle: string;
    inSubduct: string | null;
    subducts: {
      id: number;
      number: number;
      color: string | null;
      occupancy: Occupancy;
      cable: ObjectRef | null;
    }[];
  }[];
}

export type Occupancy = 'empty' | 'reserved' | 'cable' | 'blown_fibre';

export const occupancyLabels: Record<Occupancy, string> = {
  empty: 'Tom',
  reserved: 'Reserverad',
  cable: 'Kabel',
  blown_fibre: 'Blåsfiber',
};

const constructionLabels: Record<string, string> = {
  trench: 'Schakt',
  plough: 'Plöjd',
  aerial: 'Luftledning',
  existing: 'Befintlig kanalisation',
};

/** Tubes in a duct's cross-section: in a ring around the centre, one in the middle when there is a single tube. */
export function crossSection(count: number, size = 120): { x: number; y: number; r: number }[] {
  const centre = size / 2;
  if (count === 1) {
    return [{ x: centre, y: centre, r: size * 0.36 }];
  }
  const ring = count > 12 ? 2 : 1;
  const tubes: { x: number; y: number; r: number }[] = [];
  const outer = ring === 2 ? Math.ceil(count * 0.6) : count;
  const placements = ring === 2 ? [outer, count - outer] : [count];
  const radii = ring === 2 ? [size * 0.36, size * 0.18] : [size * 0.3];
  // As large as fits: neighbours on a ring touch at most (chord 2R·sin(π/n)), with a little air.
  const tube = Math.min(
    size * 0.12,
    ...placements.map((n, i) => radii[i] * Math.sin(Math.PI / Math.max(n, 2)) * 0.92),
  );
  placements.forEach((n, i) => {
    for (let k = 0; k < n; k++) {
      const angle = (k / n) * Math.PI * 2 - Math.PI / 2;
      tubes.push({
        x: centre + radii[i] * Math.cos(angle),
        y: centre + radii[i] * Math.sin(angle),
        r: tube,
      });
    }
  });
  return tubes;
}

/**
 * A route segment (ADR-0014, #236): how it is built, its ends, and each duct as a cross-section drawn from its type's
 * template, with the tubes' occupancy as status: dot and text, never colour alone.
 */
@Component({
  selector: 'cmdb-route-segment-panel',
  imports: [DecimalPipe, ObjectLinkComponent, StatusComponent, ImpactListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (segment.value(); as s) {
      <header class="header">
        <div class="kind">Trasé · {{ construction(s.construction) }}</div>
        <div class="code mono">{{ s.code }}</div>
        <div class="meta">
          <cmdb-status [value]="asLifecycle(s.lifecycle)" />
          <span>{{ s.lengthM / 1000 | number: '1.2-2' }} km</span>
          <span>{{ s.ducts.length }} dukter, {{ s.freeSubducts }} lediga rör</span>
        </div>
      </header>
      <section>
        <h3>Ändar</h3>
        <dl class="facts">
          <dt>A</dt>
          <dd><cmdb-link [ref]="s.a" [showName]="true" /></dd>
          <dt>B</dt>
          <dd><cmdb-link [ref]="s.b" [showName]="true" /></dd>
          @if (s.owner) {
            <dt>Ägare</dt>
            <dd>{{ s.owner }}</dd>
          }
        </dl>
      </section>
      <section>
        <h3>Om sträckan grävs av</h3>
        @if (impact.value(); as i) {
          <p class="muted">
            {{ i.cables?.length ?? 0 }} kablar{{
              i.hiddenCables ? ' och ' + i.hiddenCables + ' utanför ditt omfång' : ''
            }}
            kapas.
          </p>
          @if (i.cables?.length) {
            <p class="cables">
              @for (c of i.cables; track c.id; let last = $last) {
                <cmdb-link [ref]="c" />{{ last ? '' : ', ' }}
              }
            </p>
          }
        }
        <cmdb-impact-list
          [impact]="impact.value()"
          [failed]="!!impact.error()"
          none="Inga tjänster går genom sträckan."
        />
      </section>
      @for (d of s.ducts; track d.id) {
        <section class="duct">
          <h3>
            <span class="mono">{{ d.code }}</span> {{ d.typeName }}
            <span class="muted">Ø {{ d.outerDiameterMm }} mm</span>
          </h3>
          @if (d.inSubduct) {
            <p class="muted">Ligger i {{ d.inSubduct }}.</p>
          }
          <div class="body">
            <svg
              viewBox="0 0 120 120"
              width="120"
              height="120"
              role="img"
              [attr.aria-label]="'Tvärsnitt av ' + d.code"
            >
              <circle cx="60" cy="60" r="57" class="outer" />
              @for (t of tubes(d.subducts.length); track $index; let i = $index) {
                <circle
                  [attr.cx]="t.x"
                  [attr.cy]="t.y"
                  [attr.r]="t.r"
                  class="tube"
                  [attr.data-occupancy]="d.subducts[i].occupancy"
                >
                  <title>
                    {{ d.subducts[i].number }} {{ d.subducts[i].color ?? '' }}:
                    {{ occupancyLabels[d.subducts[i].occupancy] }}
                  </title>
                </circle>
                <text [attr.x]="t.x" [attr.y]="t.y + 3" class="number">
                  {{ d.subducts[i].number }}
                </text>
              }
            </svg>
            <ul class="tubes">
              @for (t of d.subducts; track t.id) {
                <li>
                  <span class="mono no">{{ t.number }}</span>
                  @if (t.color) {
                    <span class="muted">{{ t.color }}</span>
                  }
                  <span class="status" [attr.data-occupancy]="t.occupancy">
                    <span class="dot" aria-hidden="true"></span>{{ occupancyLabels[t.occupancy] }}
                  </span>
                  @if (t.cable) {
                    <cmdb-link [ref]="t.cable" />
                  }
                </li>
              }
            </ul>
          </div>
        </section>
      }
    } @else if (segment.error()) {
      <p class="muted">Sträckan finns inte eller ligger utanför ditt omfång.</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .cables {
      font-size: var(--text-sm);
    }
    .duct h3 {
      display: flex;
      gap: var(--space-2);
      align-items: baseline;
    }
    .body {
      display: flex;
      gap: var(--space-3);
      align-items: flex-start;
    }
    svg {
      flex: none;
    }
    .outer {
      fill: var(--surface-2);
      stroke: var(--border-strong);
      stroke-width: 2;
    }
    .tube {
      fill: var(--surface-1);
      stroke: var(--border-strong);
      stroke-width: 1;
    }
    .tube[data-occupancy='cable'] {
      fill: var(--status-in-service);
    }
    .tube[data-occupancy='blown_fibre'] {
      fill: var(--status-construction);
    }
    .tube[data-occupancy='reserved'] {
      fill: var(--status-planned);
    }
    .number {
      font-size: 7px;
      text-anchor: middle;
      fill: var(--text);
    }
    .tubes {
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: var(--text-sm);
      columns: 1;
    }
    .tubes li {
      display: flex;
      gap: var(--space-2);
      align-items: baseline;
      padding: 1px 0;
    }
    .no {
      min-width: 1.5em;
      text-align: right;
    }
    .status {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      border: 1px solid var(--border-strong);
      background: var(--surface-1);
    }
    [data-occupancy='cable'] .dot {
      background: var(--status-in-service);
    }
    [data-occupancy='blown_fibre'] .dot {
      background: var(--status-construction);
    }
    [data-occupancy='reserved'] .dot {
      background: var(--status-planned);
    }
    .muted {
      color: var(--text-muted);
    }
  `,
})
export class RouteSegmentPanelComponent {
  readonly id = input.required<string>();

  protected readonly segment = httpResource<RouteSegmentDetail>(
    () => `/api/route-segments/${this.id()}`,
  );
  /** A dig across the segment (#237): every cable in its ducts, loaded after the segment itself. */
  protected readonly impact = httpResource<Impact>(() =>
    this.segment.value() ? `/api/route-segments/${this.id()}/impact` : undefined,
  );
  protected readonly occupancyLabels = occupancyLabels;
  protected readonly asLifecycle = asLifecycle;
  private readonly sections = computed(() => new Map<number, ReturnType<typeof crossSection>>());

  protected construction(key: string): string {
    return constructionLabels[key] ?? key;
  }

  protected tubes(count: number): ReturnType<typeof crossSection> {
    const cache = this.sections();
    let tubes = cache.get(count);
    if (!tubes) {
      tubes = crossSection(count);
      cache.set(count, tubes);
    }
    return tubes;
  }
}
