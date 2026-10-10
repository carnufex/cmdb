import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PanelStack } from '../shell/panels';

/** GET /api/conduit/capacity (#238) */
export interface CapacityResult {
  segments: {
    id: number;
    code: string;
    construction: string;
    freeTubes: number;
    tubes: number;
    x: number;
    y: number;
  }[];
  cables: { id: number; code: string; freeFibres: number; fibres: number }[];
  truncated: boolean;
  elapsedMs: number;
}

/**
 * Free capacity (ADR-0014, #238): route segments with empty tubes and cables with free fibres; a row opens the segment or
 * cable. The conduit layer in the map shows each segment's free tubes.
 */
@Component({
  selector: 'cmdb-capacity',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="capacity">
      <h3>Ledig kapacitet</h3>
      <form class="row" (submit)="$event.preventDefault(); run()">
        <label
          >Trasé med minst
          <input
            type="number"
            min="0"
            [value]="tubes()"
            (input)="tubes.set(+$any($event.target).value)"
            class="narrow"
          />
          tomma rör</label
        >
        <label
          >Kablar med minst
          <input
            type="number"
            min="0"
            [value]="fibres()"
            (input)="fibres.set(+$any($event.target).value)"
            class="narrow"
          />
          lediga fibrer</label
        >
        <button type="submit" class="action" [disabled]="busy() || (!tubes() && !fibres())">
          Sök
        </button>
      </form>
      @if (result(); as r) {
        <p class="muted">
          {{ r.segments.length }} sträckor och {{ r.cables.length }} kablar på
          {{ r.elapsedMs }} ms{{ r.truncated ? ', de med mest ledigt först' : '' }}.
        </p>
        @if (r.segments.length) {
          <ul>
            @for (s of r.segments; track s.id) {
              <li>
                <button type="button" class="link mono" (click)="open('route-segment', s.id)">
                  {{ s.code }}
                </button>
                <span class="muted">{{ s.freeTubes }} av {{ s.tubes }} rör tomma</span>
              </li>
            }
          </ul>
        }
        @if (r.cables.length) {
          <ul>
            @for (c of r.cables; track c.id) {
              <li>
                <button type="button" class="link mono" (click)="open('cable', c.id)">
                  {{ c.code }}
                </button>
                <span class="muted">{{ c.freeFibres }} av {{ c.fibres }} fibrer lediga</span>
              </li>
            }
          </ul>
        }
      }
      @if (error(); as e) {
        <p class="error" role="alert">{{ e }}</p>
      }
    </section>
  `,
  styles: `
    .capacity {
      margin-top: var(--space-4);
      padding-top: var(--space-3);
      border-top: var(--line);
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
      font-size: var(--text-sm);
    }
    .narrow {
      width: 4rem;
    }
    ul {
      margin: var(--space-2) 0;
      padding: 0;
      list-style: none;
      font-size: var(--text-sm);
      max-height: 240px;
      overflow: auto;
    }
    li {
      display: flex;
      gap: var(--space-2);
      align-items: baseline;
      padding: 1px 0;
    }
    .link {
      padding: 0;
      border: 0;
      background: none;
      color: var(--link, var(--focus));
      cursor: pointer;
      font: inherit;
    }
    .muted {
      color: var(--text-muted);
    }
    .error {
      color: var(--status-conflict);
    }
  `,
})
export class CapacityComponent {
  private readonly http = inject(HttpClient);
  private readonly panels = inject(PanelStack);
  protected readonly tubes = signal(2);
  protected readonly fibres = signal(0);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<CapacityResult | null>(null);

  protected async run(): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      const r = await firstValueFrom(
        this.http.get<CapacityResult>(
          `/api/conduit/capacity?minFreeTubes=${this.tubes()}&minFreeFibres=${this.fibres()}&limit=100`,
        ),
      );
      this.result.set(r);
    } catch {
      this.error.set('Kapaciteten kunde inte hämtas.');
    } finally {
      this.busy.set(false);
    }
  }

  protected open(type: 'route-segment' | 'cable', id: number): void {
    this.panels.open({ type, id: String(id) });
  }
}
