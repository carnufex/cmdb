import { httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { ObjectRef } from '../objects/models';
import { ObjectLinkComponent } from '../objects/object-link';
import { ActivePlan } from './active-plan';
import { NewOperation } from './plan-model';

/** GET /api/plans/{id}/classification (#179) */
export interface PlanClassificationReport {
  plan: number;
  introduced: number;
  elapsedMs: number;
  findings: {
    site: ObjectRef;
    before: number;
    after: number;
    afterName: string;
    raised: boolean;
    because: {
      kind: 'direct' | 'contains' | 'carries';
      subject: { code: string };
      level: number;
    }[];
    unmet: {
      rule: string;
      requirement: string;
      actual: number | null;
      required: number;
      hint: string;
    }[];
    introduced: string[];
    suggestions: { title: string; operation: unknown }[];
    alternatives: {
      site: ObjectRef;
      distanceM: number;
      freeRackUnits: number;
      cables: number;
    }[];
  }[];
}

/**
 * What the active plan does to classifications (#179, ADR-0017): the sites it raises to a higher level (a new level 5 switch makes
 * its site level 5), the requirements of that level that the plan leaves unmet, operations that would meet them and
 * nearby sites that already do.
 */
@Component({
  selector: 'cmdb-plan-classification',
  imports: [DecimalPipe, ObjectLinkComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h3>Klassning och krav</h3>
    @if (report.value(); as r) {
      @for (f of r.findings; track f.site.id) {
        <article class="finding">
          <p>
            <cmdb-link [ref]="f.site" [showName]="true" />:
            <span class="status" [attr.data-critical]="f.after >= 5">
              <span class="dot" aria-hidden="true"></span>
              {{ f.raised ? 'höjs från ' + f.before + ' till ' + f.after : 'nivå ' + f.after }} ·
              {{ f.afterName }}
            </span>
            @if (because(f); as why) {
              <span class="muted">{{ why }}</span>
            }
          </p>
          @if (f.unmet.length) {
            <ul class="unmet">
              @for (u of f.unmet; track u.rule) {
                <li [attr.data-new]="f.introduced.includes(u.rule)">
                  <span class="status"><span class="dot" aria-hidden="true"></span>Saknas</span>
                  {{ u.requirement }}
                  <span class="muted">({{ u.actual ?? 'saknas' }} av {{ u.required }})</span>
                  @if (u.hint) {
                    <span class="hint">{{ u.hint }}</span>
                  }
                </li>
              }
            </ul>
          }
          @if (f.suggestions.length) {
            <ul class="suggestions" aria-label="Åtgärder">
              @for (s of f.suggestions; track s.title) {
                <li>
                  {{ s.title }}
                  <button
                    type="button"
                    class="action"
                    [disabled]="busy()"
                    (click)="add(s.operation)"
                  >
                    Lägg till i planen
                  </button>
                </li>
              }
            </ul>
          }
          @if (f.alternatives.length) {
            <p class="alternatives">
              Eller en annan site som redan klarar nivån:
              @for (a of f.alternatives; track a.site.id; let last = $last) {
                <cmdb-link [ref]="a.site" [showName]="true" />
                <span class="muted"
                  >({{ a.distanceM / 1000 | number: '1.0-1' }} km, {{ a.freeRackUnits }} U
                  lediga)</span
                >{{ last ? '' : ', ' }}
              }
            </p>
          }
        </article>
      } @empty {
        <p class="muted">Planen höjer ingen nivå och gör inga krav ouppfyllda.</p>
      }
      @if (r.introduced) {
        <p class="warn" role="status">
          Planen gör {{ r.introduced }} krav ouppfyllda. Den förs in bara med ett undantag och en
          motivering.
        </p>
      }
    } @else if (report.error()) {
      <p class="muted">Klassningen kunde inte kontrolleras.</p>
    }
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    .finding {
      padding: var(--space-2) 0;
      border-bottom: var(--line);
      font-size: var(--text-sm);
    }
    p {
      margin: 0 0 var(--space-1);
    }
    ul {
      margin: 0 0 var(--space-1);
      padding: 0;
      list-style: none;
    }
    li {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: baseline;
      padding: 2px 0;
    }
    .status {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
      font-weight: 600;
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--status-in-service);
    }
    [data-critical='true'] .dot,
    .unmet .dot {
      background: var(--status-conflict);
    }
    .hint {
      flex-basis: 100%;
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .muted {
      color: var(--text-muted);
    }
    .warn {
      color: var(--status-conflict);
      font-weight: 600;
    }
    .error {
      color: var(--status-conflict);
    }
  `,
})
export class PlanClassificationComponent {
  readonly planId = input.required<number>();

  private readonly plan = inject(ActivePlan);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly report = httpResource<PlanClassificationReport>(() =>
    this.plan.request(`/api/plans/${this.planId()}/classification`),
  );

  protected because(f: PlanClassificationReport['findings'][number]): string {
    const words = { direct: 'satt här', contains: 'innehåller', carries: 'bär tjänst' } as const;
    return f.because
      .filter((r) => r.kind !== 'direct')
      .slice(0, 2)
      .map((r) => `${words[r.kind]} ${r.subject.code} (${r.level})`)
      .join(', ');
  }

  protected async add(operation: unknown): Promise<void> {
    this.error.set(null);
    this.busy.set(true);
    try {
      await this.plan.add(operation as NewOperation);
    } catch (e: unknown) {
      const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
      this.error.set(
        body?.detail ??
          (Object.values(body?.errors ?? {})
            .flat()
            .join(' ') ||
            'Åtgärden kunde inte läggas till.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
