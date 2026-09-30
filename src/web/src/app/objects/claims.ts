import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusComponent } from '../shell/status';

export interface PlanRef {
  id: number;
  name: string;
}

export interface ReservationRef {
  id: number;
  holderKind: 'plan' | 'service';
  /** 0 when the holder is outside the caller's scope. */
  holderId: number;
  /** The plan's name or the service's code; "Dold" when hidden. */
  holder: string;
  reason: string;
  createdBy: string;
}

/** Who has a claim on a port or a fibre (#25). */
export interface ResourceClaims {
  reservation: ReservationRef | null;
  wantedBy: PlanRef[];
  /** Plans that do not build on each other want it, or a plan wants what someone else has reserved. */
  conflict: boolean;
}

/** GET /api/reservations */
export interface ReservationView {
  id: number;
  resourceKind: 'terminal' | 'conductor' | 'slot' | 'channel';
  resourceId: number;
  slot: string | null;
  label: string;
  holderKind: 'plan' | 'service';
  holderId: number;
  holder: string;
  reason: string;
  createdBy: string;
  createdAt: string;
}

/** Claims in words: the reservation, the plans that want the resource, and a conflict in coral. */
@Component({
  selector: 'cmdb-claims',
  imports: [StatusComponent],
  template: `
    @if (claims(); as c) {
      @if (c.conflict) {
        <cmdb-status value="conflict" />
      }
      @if (c.reservation; as r) {
        <div>
          Reserverad av {{ r.holderKind === 'plan' ? 'planen' : 'tjänsten' }} {{ r.holder
          }}{{ r.reason ? ' – ' + r.reason : '' }}
        </div>
      }
      @if (c.wantedBy.length) {
        <div>Önskas av {{ names(c) }}</div>
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      font-size: var(--text-sm);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClaimsComponent {
  readonly claims = input<ResourceClaims | null | undefined>(null);

  protected names(c: ResourceClaims): string {
    return c.wantedBy.map((p) => p.name).join(', ');
  }
}
