import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ActivePlan } from './active-plan';

/** GET /api/plans/{id}/materials (#291) */
export interface PlanMaterials {
  currency: string;
  lines: {
    kind: 'cable' | 'equipment' | 'construction';
    key: string;
    name: string;
    quantity: number;
    unit: string;
    unitCost: number | null;
    cost: number | null;
  }[];
  total: number;
  missing: string[];
  elapsedMs: number;
}

const kindNames = { cable: 'Kabel', equipment: 'Utrustning', construction: 'Trasé' } as const;

/** A sum of money as one reads it: "412 000 kr", or the currency code after it. */
export function money(amount: number, currency: string): string {
  const n = Math.round(amount).toLocaleString('sv-SE');
  return currency === 'SEK' ? `${n} kr` : `${n} ${currency}`;
}

/** The materials as CSV for Excel: semicolons, a byte order mark, and decimal commas. */
export function materialsCsv(m: PlanMaterials): string {
  const cell = (v: string | number | null) => {
    const text =
      v === null
        ? ''
        : typeof v === 'number'
          ? v.toLocaleString('sv-SE', { useGrouping: false })
          : v;
    return /[;"\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  };
  const rows = [
    [
      'Slag',
      'Nyckel',
      'Namn',
      'Mängd',
      'Enhet',
      `Pris per enhet (${m.currency})`,
      `Kostnad (${m.currency})`,
    ],
    ...m.lines.map((l) => [
      kindNames[l.kind],
      l.key,
      l.name,
      l.quantity,
      l.unit,
      l.unitCost,
      l.cost,
    ]),
    ['', '', 'Summa', null, '', null, m.total],
  ];
  return '﻿' + rows.map((r) => r.map(cell).join(';')).join('\r\n') + '\r\n';
}

/**
 * A plan's materials (#291): its cables by type and metres, equipment by model, new route segments by construction and
 * metres pulled through existing conduit, priced from the catalog, with a sum and export to CSV.
 */
@Component({
  selector: 'cmdb-plan-materials',
  template: `
    @if (result(); as m) {
      @if (m.lines.length) {
        <table>
          <thead>
            <tr>
              <th scope="col">Slag</th>
              <th scope="col">Namn</th>
              <th scope="col" class="num">Mängd</th>
              <th scope="col" class="num">Pris</th>
              <th scope="col" class="num">Kostnad</th>
            </tr>
          </thead>
          <tbody>
            @for (l of m.lines; track l.kind + l.key) {
              <tr>
                <td class="muted">{{ kinds[l.kind] }}</td>
                <td>{{ l.name }}</td>
                <td class="num">{{ quantity(l.quantity) }} {{ l.unit }}</td>
                <td class="num">
                  {{ l.unitCost === null ? '–' : money(l.unitCost, m.currency) + ' / ' + l.unit }}
                </td>
                <td class="num">
                  {{ l.cost === null ? 'saknar pris' : money(l.cost, m.currency) }}
                </td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr>
              <th scope="row" colspan="4">Summa</th>
              <td class="num total">{{ money(m.total, m.currency) }}</td>
            </tr>
          </tfoot>
        </table>
        @if (m.missing.length) {
          <p class="warn">
            Saknar pris i katalogen: {{ m.missing.join(', ') }}. De räknas inte med i summan.
          </p>
        }
        <p class="row">
          <button type="button" (click)="download(m)">Exportera CSV</button>
          <span class="muted">Bara planens egna ändringar, inte planerna den bygger på.</span>
        </p>
      } @else {
        <p class="muted">Planen skapar inga kablar, ingen utrustning och ingen trasé ännu.</p>
      }
    } @else if (materials.error()) {
      <p class="warn">Materialet kunde inte räknas.</p>
    } @else {
      <p class="muted">Räknar material …</p>
    }
  `,
  styles: `
    :host {
      display: block;
      overflow-x: auto;
      font-size: var(--text-sm);
    }
    table {
      width: 100%;
      border-collapse: collapse;
    }
    th {
      text-align: left;
      font-size: var(--text-xs);
      font-weight: 600;
      color: var(--text-muted);
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
    }
    td {
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
    }
    tfoot th,
    tfoot td {
      border-bottom: 0;
      font-size: var(--text-sm);
      color: var(--text);
    }
    .num {
      text-align: right;
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }
    .total {
      font-weight: 600;
    }
    .muted {
      color: var(--text-muted);
    }
    .warn {
      color: var(--status-conflict);
    }
    .row {
      display: flex;
      align-items: center;
      gap: var(--space-3);
    }
    button {
      height: 24px;
      padding: 0 var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      cursor: pointer;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlanMaterialsComponent {
  private readonly active = inject(ActivePlan);

  readonly planId = input.required<number>();
  readonly planName = input('plan');

  protected readonly materials = httpResource<PlanMaterials>(() =>
    this.active.request(`/api/plans/${this.planId()}/materials`),
  );
  protected readonly result = computed(() =>
    this.materials.hasValue() ? this.materials.value() : null,
  );
  protected readonly kinds = kindNames;
  protected readonly money = money;

  protected quantity(n: number): string {
    return n.toLocaleString('sv-SE', { maximumFractionDigits: 1 });
  }

  protected download(m: PlanMaterials): void {
    const url = URL.createObjectURL(
      new Blob([materialsCsv(m)], { type: 'text/csv;charset=utf-8' }),
    );
    const a = document.createElement('a');
    a.href = url;
    a.download = `material-${this.planName().replace(/[^\p{L}\p{N}-]+/gu, '-')}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }
}
