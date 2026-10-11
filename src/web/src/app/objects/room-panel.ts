import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  forwardRef,
  inject,
  input,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import { ActivePlan } from '../plans/active-plan';
import { Tools } from '../shell/tools';
import { ToolSizeComponent } from '../shell/tool-size';
import { ObjectLinkComponent } from './object-link';
import { RoomDrawingComponent } from './room-drawing';
import { freeText, RoomDetail, RoomRack, SiteRoom } from './room-model';

/**
 * The room view (#256): the room from above with its racks in their rows, how full each is and the highest criticality
 * in it, as the workspace (#251). A rack opens the rack view (#255); connections between racks can be drawn. In a plan,
 * the racks it adds and places elsewhere show. The room is in the address (`?tool=room&room=<id>`).
 */
@Component({
  selector: 'cmdb-room-panel',
  imports: [ToolSizeComponent, ObjectLinkComponent, RoomDrawingComponent],
  template: `
    <header class="head">
      <h2>Rumsvy</h2>
      <cmdb-tool-size />
      <button type="button" class="close" aria-label="Stäng rumsvyn" (click)="tools.close()">
        ×
      </button>
    </header>
    @if (result(); as r) {
      <div class="bar">
        <cmdb-link [ref]="r.site" />
        <strong>{{ r.building ? r.building + ' / ' : '' }}{{ r.name }}</strong>
        <span class="muted">{{ r.racks.length }} rack · {{ free() }}</span>
        <label class="check">
          <input
            type="checkbox"
            [checked]="links()"
            (change)="links.set($any($event.target).checked)"
          />
          Visa kablar mellan rack ({{ connections() }})
        </label>
      </div>
      <div class="body">
        <cmdb-room-drawing [room]="r" [showLinks]="links()" (chosen)="openRack($event)" />
        <p class="legend muted">
          Den tjocka kanten är rackets framsida. Stapeln nederst visar hur fullt racket är. En röd
          ram betyder kritikalitet {{ r.criticalFrom }} eller högre (#180).
        </p>
      </div>
    } @else if (room.error()) {
      <p class="error">Rummet finns inte eller syns inte för dig.</p>
    } @else {
      <p class="muted pad">Ritar rummet …</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      min-height: 36px;
      padding: 0 var(--space-2) 0 var(--space-4);
      border-bottom: var(--line);
      h2 {
        margin: 0;
        font-size: var(--text-md);
        font-weight: 600;
      }
    }
    .close {
      width: 26px;
      height: 26px;
      border: 0;
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-3);
      padding: var(--space-2) var(--space-4);
      border-bottom: var(--line);
      font-size: var(--text-sm);
    }
    .check {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow: auto;
      padding: var(--space-3) var(--space-4);
    }
    .legend {
      margin-top: var(--space-3);
      font-size: var(--text-sm);
    }
    .muted {
      color: var(--text-muted);
    }
    .error,
    .pad {
      padding: var(--space-3) var(--space-4);
    }
    .error {
      color: var(--status-conflict);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoomPanelComponent {
  protected readonly tools = inject(Tools);
  private readonly plan = inject(ActivePlan);

  private readonly roomId = toSignal(
    inject(ActivatedRoute).queryParamMap.pipe(map((q) => Number(q.get('room')) || null)),
    { initialValue: null },
  );
  protected readonly room = httpResource<RoomDetail>(() => {
    const id = this.roomId();
    return id ? this.plan.request(`/api/rooms/${id}${this.plan.param(true)}`) : undefined;
  });
  /** The room, or nothing while loading or failed: value() throws in the error state. */
  protected readonly result = computed(() => (this.room.hasValue() ? this.room.value() : null));
  protected readonly links = signal(false);
  protected readonly connections = computed(
    () => this.result()?.links.reduce((n, l) => n + l.connections, 0) ?? 0,
  );
  protected readonly free = computed(() => {
    const racks = this.result()?.racks ?? [];
    return freeText({
      freeUnits: racks.reduce((n, r) => n + r.freeUnits, 0),
      units: racks.reduce((n, r) => n + r.units, 0),
    });
  });

  protected openRack(r: RoomRack): void {
    this.tools.show('rack', { rack: String(r.id) });
  }
}

/**
 * The rooms of a site on its panel (#256): per building, each room with its racks and free units and a small picture
 * that opens the room view as the workspace.
 */
@Component({
  selector: 'cmdb-site-rooms',
  imports: [forwardRef(() => RoomPreviewComponent)],
  template: `
    @if (list().length) {
      <section>
        <h3>Rum</h3>
        <ul class="rooms">
          @for (r of list(); track r.id) {
            <li>
              <button
                type="button"
                class="room"
                (click)="open(r)"
                [attr.aria-label]="'Öppna rumsvyn för ' + r.name"
              >
                <cmdb-room-preview [roomId]="r.id" />
                <span class="text">
                  <strong>{{ r.building ? r.building + ' / ' : '' }}{{ r.name }}</strong>
                  <span class="muted">{{ r.racks }} rack · {{ free(r) }} · Rumsvy</span>
                </span>
              </button>
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: `
    .rooms {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }
    .room {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      width: 100%;
      padding: var(--space-1);
      border: 0;
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text);
      font: inherit;
      font-size: var(--text-sm);
      text-align: left;
      cursor: pointer;
      &:hover {
        background: var(--surface-2);
      }
    }
    cmdb-room-preview {
      width: 96px;
      flex: none;
    }
    .text {
      display: flex;
      flex-direction: column;
    }
    .muted {
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SiteRoomsComponent {
  private readonly tools = inject(Tools);
  private readonly plan = inject(ActivePlan);

  readonly siteId = input.required<number>();

  protected readonly rooms = httpResource<SiteRoom[]>(() =>
    this.plan.request(`/api/sites/${this.siteId()}/rooms`),
  );
  protected readonly list = computed(() => (this.rooms.hasValue() ? this.rooms.value() : []));

  protected readonly free = freeText;

  protected open(r: SiteRoom): void {
    this.tools.show('room', { room: String(r.id) });
  }
}

/** A small picture of a room's racks for the site panel (#256). */
@Component({
  selector: 'cmdb-room-preview',
  imports: [RoomDrawingComponent],
  template: `
    @if (result(); as d) {
      <cmdb-room-drawing [room]="d" [compact]="true" />
    }
  `,
  styles: `
    :host {
      display: block;
      pointer-events: none;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoomPreviewComponent {
  private readonly plan = inject(ActivePlan);
  readonly roomId = input.required<number>();
  private readonly room = httpResource<RoomDetail>(() =>
    this.plan.request(`/api/rooms/${this.roomId()}${this.plan.param(true)}`),
  );
  protected readonly result = computed(() => (this.room.hasValue() ? this.room.value() : null));
}
