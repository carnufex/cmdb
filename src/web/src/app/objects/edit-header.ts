import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Lifecycle, StatusComponent } from '../shell/status';

const lifecycles: { value: Lifecycle; label: string }[] = [
  { value: 'planned', label: 'Planerad' },
  { value: 'under_construction', label: 'Under byggnation' },
  { value: 'in_service', label: 'I drift' },
  { value: 'decommissioning', label: 'Under avveckling' },
  { value: 'removed', label: 'Borttagen' },
];

/**
 * Name and status edited in place: click the name to rename, pick a status. No dialogs (docs/ux.md).
 * The API refuses the change unless the user may write, and the error is shown inline.
 */
@Component({
  selector: 'cmdb-edit-header',
  imports: [StatusComponent],
  template: `
    @if (editing()) {
      <form class="rename" (submit)="$event.preventDefault(); save({ name: field.value })">
        <input #field [value]="name()" aria-label="Namn" (keydown.escape)="editing.set(false)" />
        <button type="submit">Spara</button>
        <button type="button" class="quiet" (click)="editing.set(false)">Avbryt</button>
      </form>
    } @else {
      <button type="button" class="name" title="Byt namn" (click)="editing.set(true)">
        {{ name() }}
      </button>
    }
    <label class="status">
      <cmdb-status [value]="lifecycle()" />
      <select
        aria-label="Livscykel"
        [value]="lifecycle()"
        (change)="save({ lifecycle: status.value })"
        #status
      >
        @for (l of lifecycles; track l.value) {
          <option [value]="l.value">{{ l.label }}</option>
        }
      </select>
    </label>
    @if (error()) {
      <p class="error">{{ error() }}</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-2) var(--space-3);
      margin-top: var(--space-1);
    }
    .name {
      padding: 0;
      border: 0;
      background: none;
      color: var(--text-muted);
      text-align: left;
      cursor: text;
      &:hover {
        color: var(--text);
        text-decoration: underline dotted;
      }
    }
    .rename {
      display: flex;
      gap: var(--space-2);
      width: 100%;
      input {
        flex: 1;
        min-width: 0;
        height: 26px;
        padding: 0 var(--space-2);
        background: var(--surface-2);
        color: var(--text);
        border: 1px solid var(--border-strong);
        border-radius: var(--radius-sm);
        font: inherit;
      }
      button {
        height: 26px;
        padding: 0 var(--space-3);
        border: 0;
        border-radius: var(--radius-sm);
        background: var(--action);
        color: var(--action-text);
        cursor: pointer;
        &.quiet {
          background: transparent;
          color: var(--text-muted);
          border: var(--line);
        }
      }
    }
    .status {
      position: relative;
      display: inline-flex;
      cursor: pointer;
      select {
        position: absolute;
        inset: 0;
        opacity: 0;
        cursor: pointer;
      }
    }
    .error {
      width: 100%;
      margin: 0;
      font-size: var(--text-sm);
      color: var(--status-conflict);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EditHeaderComponent {
  private readonly http = inject(HttpClient);

  /** e.g. /api/sites/42 */
  readonly url = input.required<string>();
  readonly name = input.required<string>();
  readonly lifecycle = input.required<Lifecycle>();
  readonly saved = output<void>();

  protected readonly editing = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly lifecycles = lifecycles;

  protected async save(change: { name?: string; lifecycle?: string }): Promise<void> {
    this.error.set(null);
    try {
      await firstValueFrom(this.http.patch(this.url(), change));
      this.editing.set(false);
      this.saved.emit();
    } catch (e: unknown) {
      const status = (e as { status?: number }).status;
      this.error.set(
        status === 403
          ? 'Du har inte behörighet att ändra det här.'
          : 'Ändringen kunde inte sparas.',
      );
    }
  }
}
