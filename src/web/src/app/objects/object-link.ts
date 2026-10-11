import { HttpClient } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  OnDestroy,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { IconComponent, objectIcons } from '../shell/icons';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { asLifecycle, ObjectRef, ObjectSummary, typeLabels } from './models';

const HOVER_DELAY_MS = 450;

/**
 * Every object reference is a link: click opens it in a panel on top of the stack, a short hover shows
 * a card with the key facts so a click is often unnecessary (docs/ux.md).
 */
@Component({
  selector: 'cmdb-link',
  imports: [StatusComponent, IconComponent],
  template: `
    @if (ref().id === 0) {
      <!-- Outside the caller's access scope (#22): nothing to open, nothing to show. -->
      <span class="hidden" title="Objektet ligger utanför ditt behörighetsomfång">{{
        ref().name ?? 'Utanför ditt omfång'
      }}</span>
    } @else if (ref().id < 0) {
      <!-- Planned in the active plan (#107): not in production yet, so there is no panel to open. -->
      <span class="planned" title="Planerad i planen, finns inte i produktion än">
        @if (icon()) {
          <cmdb-icon class="icon" [name]="icons[ref().type]" />
        }
        <span class="mono">{{ ref().code }}</span>
        @if (showName() && ref().name) {
          {{ ref().name }}
        }
      </span>
    } @else {
      <a
        [href]="href()"
        (click)="open($event)"
        (mouseenter)="startHover()"
        (mouseleave)="endHover()"
        (focus)="startHover()"
        (blur)="endHover()"
      >
        @if (icon()) {
          <cmdb-icon class="icon" [name]="icons[ref().type]" />
        }
        <span class="mono">{{ ref().code }}</span>
        @if (showName() && ref().name) {
          <span class="name">{{ ref().name }}</span>
        }
      </a>
    }
    @if (card(); as c) {
      <div class="card" role="tooltip">
        <div class="head">
          <span class="type"><cmdb-icon [name]="icons[c.type]" />{{ typeLabels[c.type] }}</span>
          <cmdb-status [value]="c.lifecycle" />
        </div>
        <div class="code mono">{{ c.code }}</div>
        @if (c.name) {
          <div class="title">{{ c.name }}</div>
        }
        <dl>
          @for (f of c.facts; track f.label) {
            <dt>{{ f.label }}</dt>
            <dd>{{ f.value }}</dd>
          }
        </dl>
      </div>
    }
  `,
  styles: `
    :host {
      position: relative;
      display: inline-block;
      min-width: 0;
    }
    a {
      display: inline-flex;
      gap: var(--space-2);
      align-items: center;
      max-width: 100%;
      color: var(--text);
      text-decoration: none;
      border-bottom: 1px dotted var(--border-strong);
      &:hover {
        border-bottom-color: var(--text-muted);
      }
    }
    .icon {
      align-self: center;
      color: var(--text-muted);
    }
    .name {
      color: var(--text-muted);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .card {
      position: absolute;
      top: calc(100% + 6px);
      left: 0;
      z-index: 30;
      width: 260px;
      padding: var(--space-3);
      background: var(--surface-2);
      border: var(--line);
      border-radius: var(--radius-md);
      box-shadow: 0 8px 24px color-mix(in srgb, black 30%, transparent);
      pointer-events: none;
    }
    .head {
      display: flex;
      justify-content: space-between;
      align-items: center;
    }
    .type {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
      font-size: var(--text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--text-muted);
    }
    .code {
      margin-top: var(--space-1);
    }
    .title {
      color: var(--text-muted);
    }
    dl {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-3);
      margin: var(--space-2) 0 0;
      font-size: var(--text-sm);
    }
    dt {
      color: var(--text-muted);
    }
    dd {
      margin: 0;
    }
    .planned {
      color: var(--status-planned);
    }
    .hidden {
      color: var(--text-muted);
      font-style: italic;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ObjectLinkComponent implements OnDestroy {
  private readonly panels = inject(PanelStack);
  private readonly http = inject(HttpClient);

  readonly ref = input.required<ObjectRef>();
  readonly showName = input(false);
  /** The type's icon before the code (#249); off where the row already says what it is. */
  readonly icon = input(true);

  protected readonly card = signal<ObjectSummary | null>(null);
  protected readonly typeLabels = typeLabels;
  protected readonly icons = objectIcons;
  protected readonly asLifecycle = asLifecycle;

  private timer?: ReturnType<typeof setTimeout>;

  protected href(): string {
    return `?p=${this.ref().type}:${this.ref().id}`;
  }

  protected open(event: MouseEvent): void {
    if (event.ctrlKey || event.metaKey || event.shiftKey) {
      return; // Let the browser open a new tab with the shareable URL.
    }
    event.preventDefault();
    this.endHover();
    this.panels.open({ type: this.ref().type, id: String(this.ref().id) });
  }

  protected startHover(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(async () => {
      const { type, id } = this.ref();
      try {
        this.card.set(
          await firstValueFrom(this.http.get<ObjectSummary>(`/api/summary/${type}/${id}`)),
        );
      } catch {
        this.card.set(null);
      }
    }, HOVER_DELAY_MS);
  }

  protected endHover(): void {
    clearTimeout(this.timer);
    this.card.set(null);
  }

  ngOnDestroy(): void {
    clearTimeout(this.timer);
  }
}
