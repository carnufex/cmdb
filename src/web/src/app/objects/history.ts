import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { sourceAttributeLabel } from './sources';

/** GET /api/history/{type}/{id} (#23) */
export interface ObjectHistory {
  type: string;
  id: number;
  entries: HistoryEntry[];
}

export interface HistoryEntry {
  at: string;
  kind: 'created' | 'changed' | 'deleted';
  operation: HistoryOperation | null;
  changes: FieldChange[];
}

export interface HistoryOperation {
  id: number;
  at: string;
  actor: string;
  client: string | null;
  command: string;
  why: string | null;
  chained: boolean;
}

export interface FieldChange {
  field: string;
  before: string | null;
  after: string | null;
}

const kindText: Record<HistoryEntry['kind'], string> = {
  created: 'Första registrerade version',
  changed: 'Ändrad',
  deleted: 'Borttagen',
};

const commandText: Record<string, string> = {
  update_object: 'ändring i panelen',
  apply_plan: 'införd plan',
  import: 'import',
  reconcile: 'avstämning',
  set_classification: 'klassning',
  write: 'skrivning utan kommando',
};

const lifecycleText: Record<string, string> = {
  planned: 'Planerad',
  under_construction: 'Under byggnation',
  in_service: 'I drift',
  removed: 'Borttagen',
};

/** What an entry is, in words. */
export function historyKindText(kind: HistoryEntry['kind']): string {
  return kindText[kind];
}

/** The command behind a change, in words where it has a name, otherwise as it is logged. */
export function historyCommandText(command: string): string {
  return commandText[command] ?? command;
}

/** A changed field as "Namn: före → efter"; a position only says that it moved. */
export function historyChangeText(c: FieldChange): string {
  const label = c.field === 'geom' ? 'Läge' : sourceAttributeLabel(snakeToCamel(c.field));
  if (c.field === 'geom') {
    return `${label} ändrat`;
  }
  return `${label}: ${valueText(c.field, c.before)} → ${valueText(c.field, c.after)}`;
}

function valueText(field: string, value: string | null): string {
  if (value === null || value === '') {
    return '–';
  }
  return field === 'lifecycle' ? (lifecycleText[value] ?? value) : value;
}

function snakeToCamel(field: string): string {
  return field.startsWith('attributes.')
    ? field
    : field.replace(/_([a-z])/g, (_, c: string) => c.toUpperCase());
}

/**
 * An object's history (#23): its versions newest first, what changed and the operation that changed it, with who and
 * why. Read only when opened, so a panel costs nothing extra until someone asks.
 */
@Component({
  selector: 'cmdb-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section>
      <details class="history" (toggle)="open.set($any($event.target).open)">
        <summary><h3>Historik</h3></summary>
        @if (history.hasValue()) {
          <ol class="entries">
            @for (e of entries(); track $index) {
              <li>
                <div class="head">
                  <span class="when mono">{{ when(e.at) }}</span>
                  <span class="kind">{{ kind(e.kind) }}</span>
                  @if (e.operation; as op) {
                    <span class="muted">av {{ op.actor }} · {{ command(op.command) }}</span>
                  }
                </div>
                @if (e.operation?.why; as why) {
                  <p class="why">{{ why }}</p>
                }
                @if (e.changes.length) {
                  <ul class="changes">
                    @for (c of e.changes; track c.field) {
                      <li>{{ change(c) }}</li>
                    }
                  </ul>
                }
              </li>
            }
          </ol>
          @if (entries().length === 1) {
            <p class="muted">Inga ändringar sedan objektet registrerades.</p>
          }
        } @else if (history.error()) {
          <p class="muted">Historiken kunde inte läsas.</p>
        } @else if (open()) {
          <p class="loading">Hämtar…</p>
        }
      </details>
    </section>
  `,
  styleUrl: './panel.scss',
  styles: `
    summary {
      cursor: pointer;
    }
    summary h3 {
      display: inline;
    }
    .entries {
      list-style: none;
      margin: var(--space-2) 0 0;
      padding: 0;
      display: grid;
      gap: var(--space-3);
    }
    .head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: baseline;
    }
    .kind {
      font-weight: 600;
    }
    .why {
      margin: var(--space-1) 0 0;
    }
    .changes {
      margin: var(--space-1) 0 0;
      padding-left: var(--space-4);
    }
  `,
})
export class HistoryComponent {
  readonly type = input.required<string>();
  readonly id = input.required<number>();

  protected readonly open = signal(false);
  protected readonly history = httpResource<ObjectHistory>(() =>
    this.open() ? `/api/history/${this.type()}/${this.id()}` : undefined,
  );
  protected readonly entries = computed(() =>
    this.history.hasValue() ? this.history.value().entries : [],
  );

  protected readonly kind = historyKindText;
  protected readonly command = historyCommandText;
  protected readonly change = historyChangeText;

  protected when(at: string): string {
    return new Date(at).toLocaleString('sv-SE', { dateStyle: 'short', timeStyle: 'short' });
  }
}
