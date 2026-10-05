import { HttpClient, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ActivePlan } from '../plans/active-plan';

/** GET /api/classifications/schemas (#176) */
export interface ClassificationSchema {
  key: string;
  name: string;
  description: string;
  appliesTo: string[];
  levels: { level: number; name: string; description: string }[];
  criticalFrom: number;
}

/** GET /api/classifications/derived (#177): the level once contents and carried services are counted. */
export interface DerivedClassification {
  schema: string;
  level: number;
  name: string;
  critical: boolean;
  inherited: boolean;
  reasons: {
    kind: 'direct' | 'contains' | 'carries';
    subject: { type: string; id: number; code: string; name?: string | null };
    level: number;
  }[];
  services: number;
  locationLevels: { id: number; level: number; because: string }[];
}

/** GET /api/classifications?type=&id= */
export interface ObjectClassification {
  schema: string;
  schemaName: string;
  level: number;
  name: string;
  critical: boolean;
  source: string;
  setBy: string;
  setAt: string;
}

/**
 * An object's classifications (#176, ADR-0017) and a way to change them: the level in each schema that applies to the
 * kind of object, with its name, shown as dot and text. In an open draft plan a change becomes an operation in the plan;
 * otherwise it is written directly (cmdb-full only; the server refuses anyone else).
 */
@Component({
  selector: 'cmdb-classification',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (s of schemas(); track s.key) {
      <p class="classification">
        <span class="label">{{ s.name }}</span>
        @if (levelOf(s.key); as c) {
          <span class="status" [attr.data-critical]="c.critical">
            <span class="dot" aria-hidden="true"></span>{{ c.level }} · {{ c.name }}
          </span>
        } @else {
          <span class="muted">ej satt</span>
        }
        <select
          [attr.aria-label]="s.name + ' för ' + type() + ' ' + objectId()"
          [value]="levelOf(s.key)?.level ?? ''"
          [disabled]="busy()"
          (change)="set(s.key, $any($event.target).value)"
        >
          <option value="">{{ planned() ? 'Ändra i planen…' : 'Ändra…' }}</option>
          @for (l of s.levels; track l.level) {
            <option [value]="l.level">{{ l.level }} · {{ l.name }}</option>
          }
          <option value="clear">Ta bort</option>
        </select>
      </p>
    }
    @if (derived.value(); as d) {
      @if (d.inherited) {
        <p class="derived" role="status">
          <span class="label">Härledd</span>
          <span class="status" [attr.data-critical]="d.critical">
            <span class="dot" aria-hidden="true"></span>{{ d.level }} · {{ d.name }}
          </span>
          <span class="muted">{{ because(d) }}</span>
        </p>
      }
    }
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    .classification {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin: var(--space-1) 0 0;
      font-size: var(--text-sm);
    }
    .label {
      color: var(--text-muted);
    }
    .derived {
      display: flex;
      align-items: baseline;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin: var(--space-1) 0 0;
      font-size: var(--text-sm);
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
    [data-critical='true'] .dot {
      background: var(--status-conflict);
    }
    .muted {
      color: var(--text-muted);
    }
    select {
      font: inherit;
      font-size: var(--text-xs);
      color: var(--text-muted);
      background: var(--surface-2);
      border: var(--line);
      border-radius: var(--radius-sm);
    }
    .error {
      color: var(--status-conflict);
      font-size: var(--text-sm);
      margin: var(--space-1) 0 0;
    }
  `,
})
export class ClassificationComponent {
  readonly type = input.required<'site' | 'equipment' | 'cable' | 'service'>();
  readonly objectId = input.required<number>();

  private readonly http = inject(HttpClient);
  private readonly plan = inject(ActivePlan);

  private readonly schemaList = httpResource<ClassificationSchema[]>(
    () => '/api/classifications/schemas',
  );
  private readonly current = httpResource<ObjectClassification[]>(() => ({
    url: '/api/classifications',
    params: { type: this.type(), id: this.objectId(), r: this.revision() },
  }));

  /** The level once contents and carried services count, in the active plan's view when there is one (#177). */
  protected readonly derived = httpResource<DerivedClassification>(() => {
    const schema = this.schemas()[0];
    if (!schema) {
      return undefined;
    }
    this.revision();
    return this.plan.request(
      `/api/classifications/derived?type=${this.type()}&id=${this.objectId()}&schema=${schema.key}${this.plan.param()}`,
    );
  });

  protected because(d: DerivedClassification): string {
    const words = { direct: 'satt här', contains: 'innehåller', carries: 'bär tjänst' } as const;
    return d.reasons
      .filter((r) => r.kind !== 'direct')
      .map((r) => `${words[r.kind]} ${r.subject.code} (${r.level})`)
      .join(', ');
  }

  private readonly revision = signal(0);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly planned = computed(() => this.plan.view.value()?.plan?.status === 'draft');

  protected readonly schemas = computed(() =>
    (this.schemaList.value() ?? []).filter((s) => s.appliesTo.includes(this.type())),
  );

  protected levelOf(schema: string): ObjectClassification | undefined {
    return this.current.value()?.find((c) => c.schema === schema);
  }

  protected async set(schema: string, value: string): Promise<void> {
    if (value === '') {
      return;
    }
    const level = value === 'clear' ? null : Number(value);
    this.error.set(null);
    this.busy.set(true);
    try {
      if (this.planned()) {
        await this.plan.add({
          kind: 'set_classification',
          type: this.type(),
          objectId: this.objectId(),
          schema,
          level,
        });
      } else {
        await firstValueFrom(
          this.http.put('/api/classifications', {
            type: this.type(),
            id: this.objectId(),
            schema,
            level,
          }),
        );
        this.revision.update((r) => r + 1);
      }
    } catch (e: unknown) {
      const body = e as {
        status?: number;
        error?: { detail?: string; errors?: Record<string, string[]> };
      };
      this.error.set(
        body.status === 403
          ? 'Du behöver skrivbehörighet för att ändra klassningar.'
          : (body.error?.detail ??
              (Object.values(body.error?.errors ?? {})
                .flat()
                .join(' ') ||
                'Klassningen kunde inte ändras.')),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
