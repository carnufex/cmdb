import { HttpClient } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ActivePlan } from './active-plan';

/** GET /api/routes/suggest (#171) */
export interface RouteSuggestion {
  from: { code: string };
  to: { code: string };
  fibres: number;
  note: string | null;
  elapsedMs: number;
  alternatives: {
    sites: { id: number; code: string }[];
    cables: { id: number; code: string; lengthM: number; free: number }[];
    newCable: {
      a: { code: string };
      b: { code: string };
      typeKey: string;
      straightM: number;
    } | null;
    lengthM: number;
    splices: number;
  }[];
}

/**
 * A connection between two sites (#171): the ways over existing cables that still have free fibres, or a new cable where
 * there is no way, and a click puts the chosen one in the plan as splices and, where needed, a new cable.
 */
@Component({
  selector: 'cmdb-plan-route',
  imports: [FormsModule, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h3>Ny förbindelse</h3>
    <form class="row" (submit)="$event.preventDefault(); suggest()">
      <label>Från <input name="from" [(ngModel)]="from" placeholder="Sitekod" required /></label>
      <label>Till <input name="to" [(ngModel)]="to" placeholder="Sitekod" required /></label>
      <label
        >Fibrer
        <input name="fibres" type="number" min="1" max="96" [(ngModel)]="fibres" class="narrow"
      /></label>
      <button type="submit" class="action" [disabled]="busy() || !from() || !to()">
        Föreslå väg
      </button>
    </form>
    @if (result(); as r) {
      @if (r.note) {
        <p class="muted">{{ r.note }}</p>
      }
      <ol class="alternatives">
        @for (a of r.alternatives; track $index; let i = $index) {
          <li>
            <span class="mono">{{ routeText(a) }}</span>
            <span class="muted">
              {{ a.lengthM / 1000 | number: '1.0-1' }} km, {{ a.splices }} skarvar
              @if (a.newCable) {
                , ny kabel {{ a.newCable.typeKey }} {{ a.newCable.a.code }}–{{ a.newCable.b.code }}
              }
            </span>
            <button type="button" class="action" [disabled]="busy()" (click)="add(i)">
              Lägg till i planen
            </button>
          </li>
        } @empty {
          <li class="muted">Ingen väg hittades.</li>
        }
      </ol>
    }
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    h3 {
      margin: var(--space-3) 0 var(--space-1);
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: end;
      font-size: var(--text-sm);
    }
    label {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    input {
      width: 8rem;
    }
    .narrow {
      width: 4rem;
    }
    .alternatives {
      margin: var(--space-2) 0;
      padding-left: var(--space-4);
      font-size: var(--text-sm);
    }
    li {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: baseline;
      padding: 2px 0;
    }
    .muted {
      color: var(--text-muted);
    }
    .error {
      color: var(--status-conflict);
    }
  `,
})
export class PlanRouteComponent {
  readonly planId = input.required<number>();

  private readonly http = inject(HttpClient);
  private readonly plan = inject(ActivePlan);
  protected readonly from = signal('');
  protected readonly to = signal('');
  protected readonly fibres = signal(1);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<RouteSuggestion | null>(null);

  protected routeText(a: RouteSuggestion['alternatives'][number]): string {
    return a.sites.map((s) => s.code).join(' → ');
  }

  protected async suggest(): Promise<void> {
    await this.run(async () => {
      const params = new URLSearchParams({
        from: this.from().trim(),
        to: this.to().trim(),
        fibres: String(this.fibres()),
        plan: String(this.planId()),
      });
      this.result.set(
        await firstValueFrom(this.http.get<RouteSuggestion>(`/api/routes/suggest?${params}`)),
      );
    });
  }

  protected async add(alternative: number): Promise<void> {
    await this.run(async () => {
      await firstValueFrom(
        this.http.post(`/api/plans/${this.planId()}/route`, {
          from: this.from().trim(),
          to: this.to().trim(),
          fibres: this.fibres(),
          alternative,
        }),
      );
      this.plan.changed();
      this.result.set(null);
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await action();
    } catch (e: unknown) {
      const body = e as {
        status?: number;
        error?: { detail?: string; errors?: Record<string, string[]> };
      };
      this.error.set(
        body.status === 404
          ? 'Siten finns inte.'
          : (body.error?.detail ??
              (Object.values(body.error?.errors ?? {})
                .flat()
                .join(' ') ||
                'Det gick inte.')),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
