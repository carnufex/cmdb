import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { Tools } from '../shell/tools';
import { ToolSizeComponent } from '../shell/tool-size';
import { Assistant, ChatLine } from './assistant';
import { AssistantAnswerComponent } from './assistant-answer';
import { withoutCitations } from './assistant-markdown';
import { AssistantSitesComponent } from './assistant-sites';
import { LotsAuth } from './lots-auth';

/** How near the end of the conversation counts as following it. */
const FOLLOW_PX = 48;

/** The chat as a tool in the dock: who is asking and in which context, the conversation, and the question. */
@Component({
  selector: 'cmdb-assistant-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AssistantAnswerComponent, AssistantSitesComponent, ToolSizeComponent],
  template: `
    <section class="chat" aria-label="Chatt med Lots">
      <header class="head">
        <div class="who">
          <h2>Lots · {{ assistant.context }}</h2>
          @if (lots.signedIn()) {
            <p>Du frågar som {{ lots.displayName() }}</p>
          }
        </div>
        @if (lots.signedIn()) {
          <button
            type="button"
            class="text-button"
            [attr.aria-pressed]="!!assistant.history()"
            [disabled]="assistant.busy()"
            (click)="assistant.toggleHistory()"
          >
            Historik
          </button>
          @if (assistant.lines().length > 0) {
            <button
              type="button"
              class="text-button"
              [disabled]="assistant.busy()"
              (click)="assistant.newChat()"
            >
              Ny chatt
            </button>
          }
        }
        <cmdb-tool-size />
        <button type="button" class="close" aria-label="Stäng chatten" (click)="tools.close()">
          ×
        </button>
      </header>

      @switch (lots.access()) {
        @case ('signing-in') {
          <p class="message state">
            <span class="dot running" aria-hidden="true"></span>Loggar in i Lots
          </p>
        }
        @case ('signed-in') {
          @if (assistant.history(); as history) {
            <div class="log" aria-label="Tidigare chattar">
              @if (history.failed) {
                <p class="note error" role="alert">Kunde inte hämta historiken från Lots.</p>
              } @else if (!history.threads) {
                <p class="state"><span class="dot running" aria-hidden="true"></span>Hämtar</p>
              } @else {
                @for (thread of history.threads; track thread.id) {
                  <button type="button" class="thread" (click)="open(thread.id)">
                    <span>{{ thread.title }}</span>
                    <small>{{ when(thread.updatedAt) }}</small>
                  </button>
                } @empty {
                  <p class="hint">Inga tidigare chattar än.</p>
                }
              }
            </div>
          } @else {
            <div #log class="log" role="log" aria-live="polite" tabindex="0" (scroll)="scrolled()">
              @if (assistant.lines().length === 0) {
                <p class="hint">Fråga om nätet. Svaren följer din behörighet.</p>
                <div class="suggestions">
                  @for (question of suggestions; track question) {
                    <button type="button" class="suggestion" (click)="assistant.ask(question)">
                      {{ question }}
                    </button>
                  }
                </div>
              }
              @for (line of assistant.lines(); track line.id) {
                @switch (line.role) {
                  @case ('user') {
                    <div class="user">{{ line.text }}</div>
                  }
                  @case ('assistant') {
                    <div class="answer">
                      <cmdb-assistant-answer [text]="line.text" [run]="line.run" />
                      @if (!assistant.busy() || line !== assistant.lines().at(-1)) {
                        <button
                          type="button"
                          class="copy"
                          [attr.aria-label]="'Kopiera svaret'"
                          (click)="copy(line)"
                        >
                          {{ copied() === line.id ? 'Kopierat' : 'Kopiera' }}
                        </button>
                      }
                    </div>
                  }
                  @case ('steps') {
                    <ul class="steps">
                      @for (step of line.steps; track step.id) {
                        <li [attr.data-step]="step.id">
                          <button
                            type="button"
                            class="step"
                            [disabled]="
                              !(
                                step.card ||
                                step.sites ||
                                step.problem ||
                                step.tool === 'find_sites'
                              )
                            "
                            [attr.aria-expanded]="step.open"
                            (click)="assistant.toggleStep(step.id)"
                          >
                            <span
                              class="dot"
                              [class]="'dot ' + step.state"
                              aria-hidden="true"
                            ></span>
                            <span class="step-label">
                              {{ step.label }}
                              @if (step.state === 'failed') {
                                <span> · gick inte</span>
                              } @else if (step.card && !step.sites) {
                                <span class="muted"> · {{ step.card.title }}</span>
                              }
                            </span>
                            @if (step.source) {
                              <span class="number" aria-hidden="true">{{ step.source }}</span>
                            }
                          </button>
                          @if (step.open) {
                            <div class="result">
                              @if (step.problem) {
                                <p class="problem">{{ step.problem }}</p>
                              }
                              @if (step.sites; as list) {
                                <cmdb-assistant-sites [list]="list" [stepId]="step.id" />
                              } @else if (step.loading) {
                                <p class="muted">Hämtar</p>
                              } @else if (step.card; as card) {
                                <div class="rows">
                                  @for (row of card.rows; track $index) {
                                    @if (row.ref; as ref) {
                                      <button
                                        type="button"
                                        class="row link"
                                        (click)="assistant.openObject(ref)"
                                      >
                                        <span>{{ row.label }}</span>
                                        @if (row.detail) {
                                          <small>{{ row.detail }}</small>
                                        }
                                      </button>
                                    } @else {
                                      <div class="row">
                                        <span>{{ row.label }}</span>
                                        @if (row.detail) {
                                          <small>{{ row.detail }}</small>
                                        }
                                      </div>
                                    }
                                  }
                                </div>
                                @if (card.more) {
                                  <p class="muted">{{ card.more }}</p>
                                }
                              }
                            </div>
                          }
                        </li>
                      }
                    </ul>
                  }
                  @case ('change') {
                    @if (line.change; as change) {
                      <div class="change" role="group" aria-label="Ändringsbegäran">
                        <strong>Ändringsbegäran - skriv:</strong>
                        <ul>
                          @for (summary of change.changes; track $index) {
                            <li>{{ summary }}</li>
                          }
                        </ul>
                        @for (problem of change.problems; track $index) {
                          <p class="problem" role="alert">{{ problem }}</p>
                        }
                        @switch (change.state) {
                          @case ('asking') {
                            <p>
                              {{
                                change.problems.length === 0
                                  ? 'Ska vi skriva det här?'
                                  : 'Det här går inte att skriva som det är.'
                              }}
                            </p>
                            <div class="answers">
                              <button
                                type="button"
                                class="yes"
                                [disabled]="change.problems.length > 0"
                                (click)="assistant.answer(line.id, true)"
                              >
                                Ja
                              </button>
                              <button
                                type="button"
                                class="text-button"
                                (click)="assistant.answer(line.id, false)"
                              >
                                Nej
                              </button>
                            </div>
                          }
                          @case ('writing') {
                            <p class="state">
                              <span class="dot running" aria-hidden="true"></span>Skriver
                            </p>
                          }
                          @case ('written') {
                            <p class="state">
                              <span class="dot done" aria-hidden="true"></span>Ändringen är skriven
                            </p>
                          }
                          @case ('declined') {
                            <p class="state">
                              <span class="dot off" aria-hidden="true"></span>Inget skrevs
                            </p>
                          }
                          @case ('failed') {
                            <p class="problem" role="alert">{{ change.message }}</p>
                          }
                        }
                      </div>
                    }
                  }
                  @default {
                    <div class="note" [class.error]="line.role === 'error'">
                      <span [attr.role]="line.role === 'error' ? 'alert' : null">{{
                        line.text
                      }}</span>
                      @if (line.link; as link) {
                        <a [href]="link.href" target="_blank" rel="noopener noreferrer">{{
                          link.label
                        }}</a>
                      }
                    </div>
                  }
                }
              }
              @if (waiting()) {
                <p class="state"><span class="dot running" aria-hidden="true"></span>Lots svarar</p>
              }
            </div>
            @if (!following()) {
              <button type="button" class="latest text-button" (click)="toEnd()">
                Till senaste
              </button>
            }
          }
          <form class="composer" (submit)="send($event)">
            <textarea
              #input
              rows="1"
              aria-label="Din fråga"
              [placeholder]="
                asking() ? 'Svara ja eller nej, eller fråga något annat' : 'Skriv din fråga'
              "
              [value]="draft()"
              (input)="draft.set(input.value)"
              (keydown.enter)="enter($event)"
            ></textarea>
            @if (assistant.busy()) {
              <button type="button" class="text-button" (click)="assistant.stop()">Stoppa</button>
            } @else {
              <button type="submit" class="yes" [disabled]="!canSend()">Skicka</button>
            }
          </form>
        }
        @case ('denied') {
          <p class="message" role="alert">
            Du har inte tillgång till Lots. Be en administratör om åtkomst.
          </p>
        }
        @case ('other-person') {
          <p class="message" role="alert">
            Lots svarade för en annan användare än den som är inloggad här, så chatten används inte.
            Logga ut och logga in igen.
          </p>
        }
        @default {
          <div class="message">
            @if (lots.access() === 'failed') {
              <p role="alert">Inloggningen i Lots avbröts eller blockerades av webbläsaren.</p>
            }
            <button type="button" class="text-button" (click)="lots.signIn()">
              Logga in i Lots
            </button>
          </div>
        }
      }
    </section>
  `,
  styles: `
    :host {
      display: flex;
      flex: 1;
      min-height: 0;
    }
    .chat {
      position: relative;
      display: flex;
      flex: 1;
      flex-direction: column;
      min-width: 0;
      font-size: var(--text-md);
    }
    .head {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-3);
      border-bottom: var(--line);
    }
    .who {
      flex: 1;
      min-width: 0;
    }
    h2 {
      margin: 0;
      font-size: var(--text-md);
      font-weight: 600;
    }
    .who p {
      margin: 0;
      overflow: hidden;
      color: var(--text-muted);
      font-size: var(--text-xs);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .close {
      border: 0;
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    .log {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: var(--space-3);
      min-height: 0;
      padding: var(--space-3);
      overflow-y: auto;
    }
    .hint {
      margin: 0;
      color: var(--text-muted);
    }
    .thread {
      display: flex;
      flex: none;
      flex-direction: column;
      gap: 2px;
      width: 100%;
      padding: var(--space-2);
      border: 0;
      border-bottom: var(--line);
      background: none;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .thread:hover {
      background: var(--surface-2);
    }
    .thread small {
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .suggestions {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
    }
    .suggestion {
      padding: var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: none;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .suggestion:hover {
      background: var(--surface-2);
    }
    .user {
      align-self: flex-end;
      max-width: 85%;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-3);
      overflow-wrap: anywhere;
      white-space: pre-wrap;
    }
    .copy {
      margin-top: var(--space-1);
      padding: 0;
      border: 0;
      background: none;
      color: var(--text-faint);
      font-size: var(--text-xs);
      cursor: pointer;
    }
    .copy:hover {
      color: var(--text);
    }
    @media (hover: hover) {
      .answer:not(:hover, :focus-within) .copy {
        opacity: 0;
      }
    }
    .note {
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .note.error,
    .problem {
      color: var(--status-conflict);
    }
    .note a {
      display: block;
      margin-top: var(--space-1);
    }
    .steps {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .step {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: 100%;
      padding: 2px 0;
      border: 0;
      background: none;
      color: var(--text-muted);
      font: inherit;
      font-size: var(--text-sm);
      text-align: left;
    }
    .step:not(:disabled) {
      cursor: pointer;
    }
    .step:not(:disabled):hover {
      color: var(--text);
    }
    .step:disabled {
      opacity: 1;
    }
    .step-label {
      flex: 1;
      min-width: 0;
    }
    .muted {
      margin: 0;
      color: var(--text-faint);
      font-size: var(--text-sm);
    }
    .number {
      flex: none;
      min-width: 16px;
      height: 16px;
      padding: 0 3px;
      border: var(--line);
      border-radius: var(--radius-sm);
      color: var(--text-faint);
      font-size: var(--text-xs);
      line-height: 14px;
      text-align: center;
    }
    .dot {
      flex: none;
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--text-faint);
    }
    .dot.running {
      background: var(--status-construction);
      animation: pulse 1.2s ease-in-out infinite;
    }
    .dot.done {
      background: var(--status-in-service);
    }
    .dot.failed {
      background: var(--status-conflict);
    }
    @keyframes pulse {
      50% {
        opacity: 0.35;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .dot.running {
        animation: none;
      }
    }
    .result {
      margin: var(--space-1) 0 var(--space-2) var(--space-4);
    }
    .result .problem {
      margin: 0;
      font-size: var(--text-sm);
    }
    .rows {
      max-height: 240px;
      overflow-y: auto;
      border: var(--line);
      border-radius: var(--radius-sm);
    }
    .row {
      display: flex;
      justify-content: space-between;
      gap: var(--space-2);
      width: 100%;
      padding: var(--space-1) var(--space-2);
      border: 0;
      border-bottom: var(--line);
      background: none;
      color: inherit;
      font: inherit;
      font-size: var(--text-sm);
      text-align: left;
    }
    .row small {
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .row.link {
      cursor: pointer;
    }
    .row.link:hover {
      background: var(--surface-2);
    }
    .change {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-3);
      border: 1px solid var(--border-strong);
      border-radius: var(--radius-md);
      background: var(--surface-2);
    }
    .change ul {
      margin: 0;
      padding-left: var(--space-4);
    }
    .change p {
      margin: 0;
    }
    .answers {
      display: flex;
      gap: var(--space-2);
    }
    .yes {
      padding: var(--space-1) var(--space-3);
      border: 0;
      border-radius: var(--radius-sm);
      background: var(--text);
      color: var(--surface-1);
      font: inherit;
      font-weight: 600;
      cursor: pointer;
    }
    .yes:disabled {
      opacity: 0.4;
      cursor: default;
    }
    .state {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      margin: 0;
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .latest {
      position: absolute;
      right: var(--space-3);
      bottom: 64px;
      background: var(--surface-3);
    }
    .composer {
      display: flex;
      gap: var(--space-2);
      align-items: flex-end;
      padding: var(--space-2) var(--space-3);
      border-top: var(--line);
    }
    textarea {
      flex: 1;
      min-width: 0;
      max-height: 160px;
      field-sizing: content;
      resize: none;
      font: inherit;
    }
    .message {
      margin: 0;
      padding: var(--space-3);
      font-size: var(--text-sm);
    }
    .message p {
      margin: 0 0 var(--space-2);
    }
  `,
})
export class AssistantChatComponent {
  protected readonly assistant = inject(Assistant);
  protected readonly lots = inject(LotsAuth);
  protected readonly tools = inject(Tools);
  protected readonly draft = signal('');
  protected readonly copied = signal<string | null>(null);
  protected readonly following = signal(true);
  protected readonly suggestions = [
    'Visa alla siter i Gävle med omnejd',
    'Vilka ärenden är öppna just nu?',
    'Hur många siter finns per region?',
    'Vad får jag se?',
  ];
  protected readonly canSend = computed(
    () => this.draft().trim().length > 0 && !this.assistant.busy(),
  );
  protected readonly asking = computed(() =>
    this.assistant.lines().some((l) => l.change?.state === 'asking'),
  );
  /** Nothing to show yet: neither text growing nor a step running. */
  protected readonly waiting = computed(() => {
    if (!this.assistant.busy()) {
      return false;
    }
    const last = this.assistant.lines().at(-1);
    return last?.role !== 'assistant' && !last?.steps?.some((s) => s.state === 'running');
  });
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  constructor() {
    // Opening the chat signs in by itself in the session the person has, and goes on where they were.
    void this.lots.connect();
    effect(() => {
      if (this.lots.signedIn()) {
        untracked(() => void this.assistant.restore());
      }
    });
    // The latest line stays in view while an answer grows, unless the person has scrolled away to read.
    afterRenderEffect(() => {
      this.assistant.lines();
      this.assistant.busy();
      const log = this.log()?.nativeElement;
      if (log && this.following() && !this.assistant.focused()) {
        log.scrollTop = log.scrollHeight;
      }
    });
    // A citation brings its source into view.
    afterRenderEffect(() => {
      const id = this.assistant.focused();
      if (id) {
        this.log()?.nativeElement.querySelector(`[data-step="${id}"]`)?.scrollIntoView({
          block: 'nearest',
        });
        this.assistant.focused.set(null);
      }
    });
  }

  protected open(thread: string): void {
    this.following.set(true);
    void this.assistant.openThread(thread).catch(() => {
      this.assistant.history.set({ threads: [], failed: true });
    });
  }

  protected when(at: string): string {
    return new Date(at).toLocaleString('sv-SE', { dateStyle: 'short', timeStyle: 'short' });
  }

  protected scrolled(): void {
    const log = this.log()?.nativeElement;
    if (log) {
      this.following.set(log.scrollHeight - log.scrollTop - log.clientHeight < FOLLOW_PX);
    }
  }

  protected toEnd(): void {
    const log = this.log()?.nativeElement;
    if (log) {
      log.scrollTop = log.scrollHeight;
      this.following.set(true);
    }
  }

  protected copy(line: ChatLine): void {
    void navigator.clipboard?.writeText(withoutCitations(line.text)).then(() => {
      this.copied.set(line.id);
      setTimeout(() => this.copied.set(null), 1500);
    });
  }

  protected enter(event: Event): void {
    if (!(event as KeyboardEvent).shiftKey) {
      this.send(event);
    }
  }

  protected send(event: Event): void {
    event.preventDefault();
    if (this.canSend()) {
      void this.assistant.ask(this.draft());
      this.draft.set('');
      this.following.set(true);
    }
  }
}
