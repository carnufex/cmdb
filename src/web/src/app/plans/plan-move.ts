import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ActivePlan } from './active-plan';
import { resolveSite } from './plan-model';

/**
 * Moves equipment to another rack or site, or a cable's end to another site, in the active plan (#187). Equipment that
 * changes site and a moved cable end lose their connections, and the server refuses when circuits run through them.
 */
@Component({
  selector: 'cmdb-plan-move',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (plan.view.value()?.plan?.status === 'draft') {
      <details class="move">
        <summary>{{ type() === 'cable' ? 'Flytta en ände' : 'Flytta' }}</summary>
        <form (submit)="$event.preventDefault(); move()">
          @if (type() === 'cable') {
            <label
              >Ände
              <select [value]="end()" (change)="end.set($any($event.target).value)">
                <option value="A">A</option>
                <option value="B">B</option>
              </select>
            </label>
          }
          <label
            >{{ type() === 'cable' ? 'Till site (kod)' : 'Till site (kod, tomt: samma)' }}
            <input
              [value]="site()"
              [required]="type() === 'cable'"
              (input)="site.set($any($event.target).value)"
          /></label>
          @if (type() === 'equipment') {
            <label
              >Rack
              <input [value]="rack()" (input)="rack.set($any($event.target).value)" />
            </label>
            <label
              >Enhet
              <input
                type="number"
                min="1"
                [value]="position()"
                (input)="position.set($any($event.target).value)"
                class="narrow"
              />
            </label>
          }
          <button type="submit" class="secondary" [disabled]="busy()">Flytta i planen</button>
        </form>
        @if (error(); as e) {
          <p class="error" role="alert">{{ e }}</p>
        }
      </details>
    }
  `,
  styles: `
    .move {
      margin: var(--space-2) 0 0;
      font-size: var(--text-sm);
    }
    form {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: end;
      margin-top: var(--space-1);
    }
    label {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    input {
      width: 9rem;
    }
    .narrow {
      width: 4rem;
    }
    .secondary {
      padding: var(--space-1) var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-2);
      color: var(--text);
      font: inherit;
      cursor: pointer;
    }
    .error {
      color: var(--status-conflict);
    }
  `,
})
export class PlanMoveComponent {
  readonly type = input.required<'equipment' | 'cable'>();
  readonly objectId = input.required<number>();
  /** Equipment: the site it stands on, so an empty site field means a move within it. */
  readonly currentSite = input<number | null>(null);

  protected readonly plan = inject(ActivePlan);
  private readonly http = inject(HttpClient);
  protected readonly end = signal<'A' | 'B'>('A');
  protected readonly site = signal('');
  protected readonly rack = signal('');
  protected readonly position = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async move(): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      const code = this.site().trim();
      const siteId = code === '' ? this.currentSite() : await this.siteId(code);
      if (siteId === null) {
        throw new Error(code === '' ? 'Ange en site.' : `Hittade ingen site med koden ${code}.`);
      }
      const rack = this.rack().trim();
      const position = Number(this.position());
      await this.plan.add(
        this.type() === 'cable'
          ? { kind: 'move', type: 'cable', objectId: this.objectId(), siteId, end: this.end() }
          : {
              kind: 'move',
              type: 'equipment',
              objectId: this.objectId(),
              siteId,
              ...(rack ? { rack } : {}),
              ...(position > 0 ? { position } : {}),
            },
      );
    } catch (e: unknown) {
      const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
      this.error.set(
        body?.detail ??
          (Object.values(body?.errors ?? {})
            .flat()
            .join(' ') ||
            (e instanceof Error ? e.message : 'Flytten kunde inte läggas till.')),
      );
    } finally {
      this.busy.set(false);
    }
  }

  private async siteId(code: string): Promise<number | null> {
    return resolveSite(code, this.plan.view.value()?.planned?.sites ?? [], (q) =>
      firstValueFrom(
        this.http.get<{ type: string; id: number; code: string }[]>(
          `/api/search?q=${encodeURIComponent(q)}&limit=10`,
        ),
      ),
    );
  }
}
