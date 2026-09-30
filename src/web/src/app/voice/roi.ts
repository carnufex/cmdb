import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';

/** The assumptions of the ROI model (#138). Every one is an input: the defaults are examples, not claims. */
export interface RoiAssumptions {
  callsPerYear: number;
  minutesSavedPerCall: number;
  nocCostPerHour: number;
  criticalOutagesPerYear: number;
  minutesFasterPerOutage: number;
  outageCostPerHour: number;
  dispatchesAvoidedPerYear: number;
  costPerDispatch: number;
  preventedOutagesPerYear: number;
  costPerPreventedOutage: number;
  solutionCostPerYear: number;
}

export const EXAMPLE: RoiAssumptions = {
  callsPerYear: 6000,
  minutesSavedPerCall: 6,
  nocCostPerHour: 700,
  criticalOutagesPerYear: 40,
  minutesFasterPerOutage: 20,
  outageCostPerHour: 50000,
  dispatchesAvoidedPerYear: 60,
  costPerDispatch: 8000,
  preventedOutagesPerYear: 1,
  costPerPreventedOutage: 400000,
  solutionCostPerYear: 600000,
};

export interface RoiResult {
  intake: number;
  mttr: number;
  dispatches: number;
  prevented: number;
  total: number;
  net: number;
  /** Months until the yearly benefit has paid the yearly cost; null when it never does. */
  paybackMonths: number | null;
}

/** Yearly value in SEK, line by line, from the assumptions. */
export function roi(a: RoiAssumptions): RoiResult {
  const intake = (a.callsPerYear * a.minutesSavedPerCall * a.nocCostPerHour) / 60;
  const mttr = (a.criticalOutagesPerYear * a.minutesFasterPerOutage * a.outageCostPerHour) / 60;
  const dispatches = a.dispatchesAvoidedPerYear * a.costPerDispatch;
  const prevented = a.preventedOutagesPerYear * a.costPerPreventedOutage;
  const total = intake + mttr + dispatches + prevented;
  return {
    intake,
    mttr,
    dispatches,
    prevented,
    total,
    net: total - a.solutionCostPerYear,
    paybackMonths: total > 0 ? (a.solutionCostPerYear / total) * 12 : null,
  };
}

interface Field {
  key: keyof RoiAssumptions;
  label: string;
  unit: string;
}

/**
 * The ROI model of the operations agent (#138): four sources of value, each from assumptions the viewer sets.
 * Nothing is claimed; the defaults are examples to be replaced with an organisation's own numbers.
 */
@Component({
  selector: 'cmdb-voice-roi',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="muted">Exempelvärden. Byt mot egna siffror, så räknas allt om.</p>
    <div class="fields">
      @for (f of fields; track f.key) {
        <label>
          <span>{{ f.label }}</span>
          <input
            type="number"
            min="0"
            [value]="assumptions()[f.key]"
            (input)="set(f.key, $any($event.target).valueAsNumber)"
          />
          <span class="unit">{{ f.unit }}</span>
        </label>
      }
    </div>
    <table>
      <tbody>
        <tr>
          <th scope="row">Mottagning: kortare tid per anmälan</th>
          <td>{{ kr(result().intake) }}</td>
        </tr>
        <tr>
          <th scope="row">Kortare tid till åtgärd vid kritiska fel</th>
          <td>{{ kr(result().mttr) }}</td>
        </tr>
        <tr>
          <th scope="row">Undvikna onödiga utryckningar</th>
          <td>{{ kr(result().dispatches) }}</td>
        </tr>
        <tr>
          <th scope="row">Förhindrade avbrott (proaktivt)</th>
          <td>{{ kr(result().prevented) }}</td>
        </tr>
        <tr class="sum">
          <th scope="row">Nytta per år</th>
          <td>{{ kr(result().total) }}</td>
        </tr>
        <tr>
          <th scope="row">Lösningens kostnad per år</th>
          <td>{{ kr(assumptions().solutionCostPerYear) }}</td>
        </tr>
        <tr class="sum">
          <th scope="row">Netto per år</th>
          <td>{{ kr(result().net) }}</td>
        </tr>
        <tr>
          <th scope="row">Återbetalningstid</th>
          <td>
            @if (result().paybackMonths; as m) {
              {{ m | number: '1.0-1' }} månader
            } @else {
              –
            }
          </td>
        </tr>
      </tbody>
    </table>
  `,
  styles: `
    .muted {
      margin: 0 0 var(--space-2);
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .fields {
      display: grid;
      gap: var(--space-1);
      margin-bottom: var(--space-3);
    }
    label {
      display: grid;
      grid-template-columns: 1fr 96px 56px;
      align-items: center;
      gap: var(--space-2);
      font-size: var(--text-xs);
    }
    input {
      width: 100%;
      padding: 2px var(--space-1);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-1);
      color: var(--text);
      font: inherit;
      text-align: right;
    }
    .unit {
      color: var(--text-muted);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-sm);
    }
    th,
    td {
      padding: 2px 0;
      border-bottom: var(--line);
      text-align: left;
      font-weight: 400;
    }
    td {
      text-align: right;
      font-variant-numeric: tabular-nums;
    }
    .sum th,
    .sum td {
      font-weight: 600;
    }
  `,
  imports: [DecimalPipe],
})
export class VoiceRoiComponent {
  protected readonly fields: Field[] = [
    { key: 'callsPerYear', label: 'Felanmälningar per år', unit: 'st' },
    { key: 'minutesSavedPerCall', label: 'Sparad NOC-tid per anmälan', unit: 'min' },
    { key: 'nocCostPerHour', label: 'Kostnad NOC per timme', unit: 'kr' },
    { key: 'criticalOutagesPerYear', label: 'Kritiska fel per år', unit: 'st' },
    { key: 'minutesFasterPerOutage', label: 'Snabbare åtgärd per kritiskt fel', unit: 'min' },
    { key: 'outageCostPerHour', label: 'Kostnad per avbrottstimme', unit: 'kr' },
    { key: 'dispatchesAvoidedPerYear', label: 'Undvikna onödiga utryckningar', unit: 'st/år' },
    { key: 'costPerDispatch', label: 'Kostnad per utryckning', unit: 'kr' },
    { key: 'preventedOutagesPerYear', label: 'Förhindrade avbrott', unit: 'st/år' },
    { key: 'costPerPreventedOutage', label: 'Kostnad per avbrott', unit: 'kr' },
    { key: 'solutionCostPerYear', label: 'Lösningens kostnad per år', unit: 'kr' },
  ];

  protected readonly assumptions = signal<RoiAssumptions>({ ...EXAMPLE });
  protected readonly result = computed(() => roi(this.assumptions()));

  protected set(key: keyof RoiAssumptions, value: number): void {
    this.assumptions.update((a) => ({
      ...a,
      [key]: Number.isFinite(value) && value >= 0 ? value : 0,
    }));
  }

  protected kr(value: number): string {
    return `${Math.round(value).toLocaleString('sv-SE')} kr`;
  }
}
