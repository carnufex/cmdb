import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AISLE, freeText, RACK_H, RACK_W, RoomDetail, RoomRack, roomLayout } from './room-model';

/**
 * A room from above (#256): racks as boxes in their rows, the front edge marked so aisles show, how full each is and
 * the highest criticality in it. A plan's new racks are dashed and racks it places elsewhere outlined in the plan's
 * colour. Connections between racks can be drawn as lines.
 */
@Component({
  selector: 'cmdb-room-drawing',
  template: `
    <svg
      [attr.viewBox]="'-8 -8 ' + (layout().width + 16) + ' ' + (layout().height + 16)"
      [attr.width]="compact() ? null : layout().width + 16"
      role="img"
      [attr.aria-label]="'Rummet ' + room().name + ' sett uppifrån'"
    >
      @if (!compact()) {
        @for (row of layout().rows; track row.label) {
          <text class="row" [attr.x]="0" [attr.y]="row.y + 14">{{ row.label }}</text>
        }
      }
      @if (showLinks()) {
        @for (l of layout().links; track $index) {
          <line class="link" [attr.x1]="l.x1" [attr.y1]="l.y1" [attr.x2]="l.x2" [attr.y2]="l.y2" />
        }
      }
      @for (p of layout().racks; track p.rack.id) {
        <g
          class="rack"
          [class.new]="p.rack.plan === 'new'"
          [class.moved]="p.rack.plan === 'moved'"
          [class.critical]="critical(p.rack)"
          [attr.transform]="'translate(' + p.x + ' ' + p.y + ')'"
          [attr.tabindex]="compact() || p.rack.id < 0 ? null : 0"
          [attr.role]="compact() ? null : 'button'"
          [attr.aria-label]="label(p.rack)"
          (click)="choose(p.rack)"
          (keydown.enter)="choose(p.rack)"
        >
          <rect class="box" [attr.width]="w" [attr.height]="h" rx="3" />
          <rect class="fill" [attr.y]="h - 6" [attr.width]="w * used(p.rack)" height="6" rx="1" />
          @if (p.rack.facing) {
            <rect
              class="front"
              [attr.y]="p.rack.facing === 'front' ? h - 3 : 0"
              [attr.width]="w"
              height="3"
            />
          }
          @if (!compact()) {
            <text class="name" x="8" y="20">{{ p.rack.name }}</text>
            <text class="meta" x="8" y="38">{{ free(p.rack) }}</text>
            <text class="meta" x="8" y="54">{{ p.rack.equipment }} enheter</text>
            @if (p.rack.criticality !== null) {
              <circle class="dot" [attr.cx]="w - 14" cy="16" r="5" />
              <text class="level" [attr.x]="w - 24" y="20" text-anchor="end">
                {{ p.rack.criticality }}
              </text>
            }
            @if (p.rack.plan) {
              <text class="plan" x="8" y="70">
                {{ p.rack.plan === 'new' ? 'nytt i planen' : 'flyttas i planen' }}
              </text>
            }
          }
        </g>
      }
      @if (showLinks()) {
        @for (l of layout().links; track $index) {
          <text
            class="count"
            [attr.x]="(l.x1 + l.x2) / 2"
            [attr.y]="(l.y1 + l.y2) / 2 - 4"
            text-anchor="middle"
          >
            {{ l.connections }}
          </text>
        }
      }
    </svg>
  `,
  styles: `
    :host {
      display: block;
    }
    svg {
      display: block;
      max-width: 100%;
      height: auto;
      font-family: var(--font-sans);
    }
    .row {
      font-size: 12px;
      font-weight: 600;
      fill: var(--text-muted);
    }
    .rack {
      cursor: pointer;
      outline: none;
      .box {
        fill: var(--surface-2);
        stroke: var(--border-strong);
        stroke-width: 1;
      }
      .fill {
        fill: color-mix(in srgb, var(--focus) 55%, transparent);
      }
      .front {
        fill: var(--text);
      }
      &:hover .box,
      &:focus-visible .box {
        stroke: var(--focus);
        stroke-width: 2;
      }
      &.critical .box {
        stroke: var(--status-conflict);
      }
      &.new .box {
        stroke: var(--status-planned);
        stroke-dasharray: 6 4;
        stroke-width: 2;
      }
      &.moved .box {
        stroke: var(--status-planned);
        stroke-width: 2;
      }
    }
    .name {
      font-size: 13px;
      font-weight: 600;
      fill: var(--text);
    }
    .meta,
    .plan {
      font-size: 11px;
      fill: var(--text-muted);
    }
    .plan {
      fill: var(--status-planned);
    }
    .dot {
      fill: var(--status-construction);
    }
    .critical .dot {
      fill: var(--status-conflict);
    }
    .level {
      font-size: 11px;
      fill: var(--text);
    }
    .link {
      stroke: var(--focus);
      stroke-width: 2;
      opacity: 0.7;
      pointer-events: none;
    }
    .count {
      font-size: 11px;
      fill: var(--text);
      paint-order: stroke;
      stroke: var(--bg);
      stroke-width: 3px;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoomDrawingComponent {
  readonly room = input.required<Pick<RoomDetail, 'name' | 'racks' | 'links' | 'criticalFrom'>>();
  readonly showLinks = input(false);
  /** A small picture for the site panel: boxes only, no text, nothing to click. */
  readonly compact = input(false);
  readonly chosen = output<RoomRack>();

  protected readonly w = RACK_W;
  protected readonly h = RACK_H;
  protected readonly aisle = AISLE;
  protected readonly layout = computed(() => roomLayout(this.room()));
  protected readonly free = freeText;

  protected used(r: RoomRack): number {
    return r.units ? (r.units - r.freeUnits) / r.units : 0;
  }

  protected critical(r: RoomRack): boolean {
    return r.criticality !== null && r.criticality >= this.room().criticalFrom;
  }

  protected label(r: RoomRack): string {
    const level = r.criticality !== null ? `, kritikalitet ${r.criticality}` : '';
    return `${r.name}: ${freeText(r)}, ${r.equipment} enheter${level}. Öppna rackvyn.`;
  }

  protected choose(r: RoomRack): void {
    if (!this.compact() && r.id > 0) {
      this.chosen.emit(r);
    }
  }
}
