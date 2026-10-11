import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  Injectable,
  signal,
  viewChild,
} from '@angular/core';

/**
 * How the network is drawn at a time (#23, ADR-0013): `reality` is what was in service then, as we know it today;
 * `knowledge` is what the CMDB said then, before later corrections.
 */
export type TimeMode = 'reality' | 'knowledge';

export interface TimePoint {
  /** The time, as ISO 8601. */
  at: string;
  /** The day it was chosen as, yyyy-mm-dd. */
  day: string;
  mode: TimeMode;
}

/** The tile query for a time: nothing for now. */
export function timeQuery(point: TimePoint | null): string {
  return timeParam(point, true);
}

/** The time as a query parameter for any graph question (#302), first or after others; nothing for now. */
export function timeParam(point: TimePoint | null, first = false): string {
  return point ? `${first ? '?' : '&'}at=${encodeURIComponent(point.at)}&mode=${point.mode}` : '';
}

/** The end of a chosen day in the browser's time zone: the network as it stood that evening. */
export function endOfDay(day: string): string {
  const [y, m, d] = day.split('-').map(Number);
  return new Date(y, m - 1, d, 23, 59, 59).toISOString();
}

/** What the banner over the map says. */
export function timeBannerText(point: TimePoint): string {
  const day = new Date(point.at).toLocaleDateString('sv-SE', { dateStyle: 'long' });
  return point.mode === 'reality'
    ? `Nätet som det var ${day}, enligt vad vi vet i dag`
    : `Nätet som CMDB:n beskrev det ${day}`;
}

/** Today as yyyy-mm-dd in the browser's time zone. */
export function today(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

/** The time the map is drawn at; null is now. */
@Injectable({ providedIn: 'root' })
export class TimeTravel {
  readonly point = signal<TimePoint | null>(null);

  go(day: string, mode: TimeMode): void {
    this.point.set({ at: endOfDay(day), day, mode });
  }

  back(): void {
    this.point.set(null);
  }
}

/**
 * *Tidsresa* in the toolbar (#23): a day and a mode, and the map is drawn as it was. The banner over the map says so and
 * has the way back to now.
 */
@Component({
  selector: 'cmdb-time-travel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown.escape)': 'open.set(false)' },
  template: `
    <button
      type="button"
      class="text-button tool-button"
      [attr.aria-expanded]="open()"
      [attr.aria-pressed]="time.point() !== null"
      aria-controls="time-travel"
      (click)="toggle()"
    >
      Tidsresa
    </button>
    @if (open()) {
      <form id="time-travel" class="popover" (submit)="$event.preventDefault(); show()">
        <label class="field">
          Datum
          <input
            #dayInput
            type="date"
            [value]="day()"
            [max]="max"
            required
            (change)="day.set($any($event.target).value)"
          />
        </label>
        <fieldset>
          <legend>Visa</legend>
          <label>
            <input
              type="radio"
              name="mode"
              value="reality"
              [checked]="mode() === 'reality'"
              (change)="mode.set('reality')"
            />
            <span><strong>Verklighet</strong> – det som var i drift då, som vi vet i dag</span>
          </label>
          <label>
            <input
              type="radio"
              name="mode"
              value="knowledge"
              [checked]="mode() === 'knowledge'"
              (change)="mode.set('knowledge')"
            />
            <span
              ><strong>Vad vi visste</strong> – det CMDB:n beskrev då, före senare rättelser</span
            >
          </label>
        </fieldset>
        <div class="buttons">
          <button type="submit" class="primary">Visa kartan</button>
          @if (time.point()) {
            <button type="button" class="text-button" (click)="time.back(); open.set(false)">
              Tillbaka till nu
            </button>
          }
        </div>
      </form>
    }
  `,
  styles: `
    :host {
      position: relative;
      display: inline-flex;
    }
    .popover {
      position: absolute;
      top: calc(100% + var(--space-2));
      left: 0;
      z-index: 30;
      width: 20rem;
      display: grid;
      gap: var(--space-3);
      padding: var(--space-3);
      background: var(--surface-1);
      border: 1px solid var(--border-strong);
      border-radius: var(--radius-md);
      box-shadow: 0 8px 24px rgb(0 0 0 / 0.25);
      color: var(--text);
    }
    .field {
      display: grid;
      gap: var(--space-1);
    }
    fieldset {
      border: 0;
      margin: 0;
      padding: 0;
      display: grid;
      gap: var(--space-2);
    }
    legend {
      padding: 0;
      margin-bottom: var(--space-1);
    }
    fieldset label {
      display: flex;
      gap: var(--space-2);
      align-items: flex-start;
    }
    .primary {
      background: var(--action);
      color: var(--action-text);
      border: 0;
      font-weight: 600;
    }
    .buttons {
      display: flex;
      gap: var(--space-2);
      align-items: center;
    }
  `,
})
export class TimeTravelComponent {
  protected readonly time = inject(TimeTravel);
  protected readonly open = signal(false);
  protected readonly max = today();
  protected readonly day = signal(
    this.time.point()?.day ?? `${new Date().getFullYear() - 10}-01-01`,
  );
  protected readonly mode = signal<TimeMode>(this.time.point()?.mode ?? 'reality');
  private readonly dayInput = viewChild<ElementRef<HTMLInputElement>>('dayInput');

  protected toggle(): void {
    this.open.update((o) => !o);
    if (this.open()) {
      queueMicrotask(() => this.dayInput()?.nativeElement.focus());
    }
  }

  protected show(): void {
    if (!this.day()) {
      return;
    }
    this.time.go(this.day(), this.mode());
    this.open.set(false);
  }
}
