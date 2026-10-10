import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  Signal,
} from '@angular/core';
import { CatalogImages } from './catalog-images';
import { PanelPort, PortStatus, portStatus, portStatusLabels } from './front-panel-model';
import { EquipmentDetail, PanelSide } from './models';
import { PANEL, RackBlock, RackDetail, rackLayout, RAIL, UNIT } from './rack-model';

interface DrawnPort {
  terminalId: number;
  name: string;
  status: PortStatus;
  x: number;
  y: number;
  w: number;
  h: number;
}

interface DrawnBlock {
  block: RackBlock;
  /** The picture of the side shown, null for a plain face. */
  image: Signal<string | null> | null;
  ports: DrawnPort[];
}

/**
 * A rack drawn with its equipment (#255): the unit numbers on the rails, each model's picture on its units (#214), or
 * a plain face with name, model and port grid when the catalog has none, the ports coloured by status, and the free
 * units. In a plan, new equipment is dashed in the planned colour and what the plan removes is struck through.
 */
@Component({
  selector: 'cmdb-rack-drawing',
  template: `
    <svg
      [attr.viewBox]="'0 0 ' + width + ' ' + height()"
      role="img"
      [attr.aria-label]="'Rack ' + rack().name + ', ' + rack().units + ' U, ' + sideLabel()"
    >
      <rect
        class="frame"
        x="0.5"
        y="0.5"
        [attr.width]="width - 1"
        [attr.height]="height() - 1"
        rx="6"
      />
      @for (u of unitNumbers(); track u) {
        <text class="u" [attr.x]="rail / 2" [attr.y]="unitY(u) + unit / 2 + 9">{{ u }}</text>
        <text class="u" [attr.x]="width - rail / 2" [attr.y]="unitY(u) + unit / 2 + 9">
          {{ u }}
        </text>
      }
      @for (u of layout().free; track u) {
        <rect
          class="free"
          [class.pickable]="plan()"
          [attr.x]="rail"
          [attr.y]="unitY(u) + 1"
          [attr.width]="panel"
          [attr.height]="unit - 2"
          (click)="plan() && freeUnit.emit(u)"
        >
          <title>{{ plan() ? 'Ny utrustning här (U ' + u + ')' : 'Ledig, U ' + u }}</title>
        </rect>
      }
      @for (d of drawn(); track d.block.item.id) {
        <g
          class="equipment"
          [class.planned]="d.block.planned"
          [class.removed]="d.block.removed"
          [attr.transform]="'translate(' + rail + ' ' + (pad + d.block.top) + ')'"
          (click)="openEquipment.emit(d.block.item.id)"
          (mouseenter)="hover.emit({ id: d.block.item.id, event: $event })"
          (mouseleave)="hover.emit(null)"
        >
          @if (d.image?.(); as url) {
            <image
              [attr.href]="url"
              x="0"
              y="0"
              [attr.width]="panel"
              [attr.height]="d.block.height"
              preserveAspectRatio="none"
            />
          } @else {
            <rect
              class="face"
              x="1"
              y="1"
              [attr.width]="panel - 2"
              [attr.height]="d.block.height - 2"
              rx="4"
            />
            <text class="name" x="28" [attr.y]="Math.min(d.block.height / 2, 40) + 4">
              {{ d.block.item.name }}
            </text>
            <text class="model" x="28" [attr.y]="Math.min(d.block.height / 2, 40) + 34">
              {{ d.block.item.model }}
            </text>
          }
          @for (p of d.ports; track p.terminalId) {
            <rect
              [class]="'port ' + p.status"
              [class.selected]="p.terminalId === selectedPort()"
              [attr.x]="p.x"
              [attr.y]="p.y"
              [attr.width]="p.w"
              [attr.height]="p.h"
              rx="3"
              (click)="$event.stopPropagation(); selectPort.emit(p.terminalId)"
            >
              <title>{{ d.block.item.name }} · {{ p.name }} · {{ labels[p.status] }}</title>
            </rect>
          }
          @if (d.block.planned) {
            <rect
              class="plan-outline"
              x="2"
              y="2"
              [attr.width]="panel - 4"
              [attr.height]="d.block.height - 4"
              rx="4"
            />
          }
          @if (d.block.removed) {
            <line
              class="strike"
              x1="0"
              [attr.y1]="d.block.height / 2"
              [attr.x2]="panel"
              [attr.y2]="d.block.height / 2"
            />
          }
        </g>
      }
    </svg>
  `,
  styles: `
    :host {
      display: block;
    }
    svg {
      display: block;
      width: 100%;
      height: auto;
    }
    .frame {
      fill: var(--surface-2);
      stroke: var(--border-strong);
    }
    .u {
      fill: var(--text-muted);
      font-size: 26px;
      text-anchor: middle;
      font-family: var(--font-mono);
    }
    .free {
      fill: transparent;
      stroke: var(--border);
      stroke-dasharray: 6 6;
      &.pickable {
        cursor: pointer;
        &:hover {
          fill: color-mix(in srgb, var(--status-planned) 18%, transparent);
        }
      }
    }
    .equipment {
      cursor: pointer;
      &.removed {
        opacity: 0.45;
      }
    }
    .face {
      fill: var(--surface-1);
      stroke: var(--border-strong);
    }
    .name {
      fill: var(--text);
      font-size: 26px;
      font-family: var(--font-mono);
    }
    .model {
      fill: var(--text-muted);
      font-size: 22px;
    }
    .port {
      fill: var(--surface-2);
      stroke: var(--border-strong);
      stroke-width: 1.5;
      fill-opacity: 0.85;
      &.connected {
        fill: var(--status-in-service);
      }
      &.planned {
        fill: var(--status-planned);
      }
      &.reserved {
        fill: var(--status-construction);
      }
      &.conflict {
        fill: var(--status-conflict);
      }
      &.selected {
        stroke: var(--focus);
        stroke-width: 5;
      }
    }
    .plan-outline {
      fill: none;
      stroke: var(--status-planned);
      stroke-width: 5;
      stroke-dasharray: 18 10;
    }
    .strike {
      stroke: var(--status-conflict);
      stroke-width: 6;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RackDrawingComponent {
  private readonly images = inject(CatalogImages);

  readonly rack = input.required<RackDetail>();
  /** Production equipment's ports and pictures, by id, as they load. */
  readonly details = input<Readonly<Record<number, EquipmentDetail>>>({});
  readonly side = input<PanelSide>('front');
  readonly selectedPort = input<number | null>(null);
  /** In an active plan a free unit can take new equipment. */
  readonly plan = input(false);

  readonly openEquipment = output<number>();
  readonly selectPort = output<number>();
  readonly freeUnit = output<number>();
  readonly hover = output<{ id: number; event: MouseEvent } | null>();

  protected readonly Math = Math;
  protected readonly labels = portStatusLabels;
  protected readonly unit = UNIT;
  protected readonly panel = PANEL;
  protected readonly rail = RAIL;
  protected readonly pad = 12;
  protected readonly width = PANEL + 2 * RAIL;

  protected readonly layout = computed(() => rackLayout(this.rack()));
  protected readonly height = computed(() => this.rack().units * UNIT + 2 * this.pad);
  protected readonly sideLabel = computed(() => (this.side() === 'front' ? 'framsida' : 'baksida'));
  protected readonly unitNumbers = computed(() =>
    Array.from({ length: this.rack().units }, (_, i) => i + 1),
  );

  protected unitY(u: number): number {
    return this.pad + (this.rack().units - u) * UNIT;
  }

  /** Each block with the picture of the side shown and its ports on it, or on the plain face's grid. */
  protected readonly drawn = computed<DrawnBlock[]>(() => {
    const side = this.side();
    const details = this.details();
    return this.layout().blocks.map((block) => {
      const detail = block.planned ? undefined : details[block.item.id];
      const images = detail?.panel.images ?? block.item.images ?? null;
      const picture = images?.[side] ?? null;
      const ports = detail ? this.ports(detail, block, side, picture) : [];
      return { block, image: picture ? this.images.url(picture.file) : null, ports };
    });
  });

  /** The ports to draw: on the picture where they are placed on this side, else as a grid on the front's plain face. */
  private ports(
    detail: EquipmentDetail,
    block: RackBlock,
    side: PanelSide,
    picture: { width: number; height: number } | null,
  ): DrawnPort[] {
    const status = (p: PanelPort) => portStatus(p);
    if (picture) {
      const sx = PANEL / picture.width;
      const sy = block.height / picture.height;
      return detail.ports
        .filter((p) => p.box?.side === side)
        .map((p) => ({
          terminalId: p.terminalId,
          name: p.name,
          status: status(p),
          x: p.box!.x * sx,
          y: p.box!.y * sy,
          w: p.box!.width * sx,
          h: p.box!.height * sy,
        }));
    }
    if (side !== 'front' || !detail.ports.length) {
      return [];
    }
    // The grid to the right of the name, as large as the units allow.
    const { rows, columns } = detail.panel;
    const left = 600;
    const gap = 4;
    const cell = Math.min(
      (PANEL - left - 24) / Math.max(1, columns) - gap,
      (block.height - 16) / Math.max(1, rows) - gap,
      40,
    );
    const top = (block.height - rows * (cell + gap)) / 2;
    return detail.ports.map((p) => ({
      terminalId: p.terminalId,
      name: p.name,
      status: status(p),
      x: left + p.column * (cell + gap),
      y: top + p.row * (cell + gap),
      w: cell,
      h: cell,
    }));
  }
}
