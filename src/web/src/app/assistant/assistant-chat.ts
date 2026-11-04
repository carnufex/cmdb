import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { VoiceCall } from '../voice/voice-call';
import { Assistant } from './assistant';
import { LotsAuth } from './lots-auth';

/** The chat: who is asking and in which context, the conversation, and the question. Answers are plain text. */
@Component({
  selector: 'cmdb-assistant-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (assistant.open()) {
      <section class="chat" [class.beside-call]="call.current()" aria-label="Chatt med Lots">
        <header>
          <div>
            <h2>Lots · {{ assistant.context }}</h2>
            @if (lots.signedIn()) {
              <p>Du frågar som {{ lots.displayName() }}</p>
            }
          </div>
          <button type="button" aria-label="Stäng chatten" (click)="assistant.toggle()">×</button>
        </header>

        @switch (lots.access()) {
          @case ('signed-in') {
            <div #log class="log" role="log" aria-live="polite" tabindex="0">
              @if (assistant.lines().length === 0) {
                <p class="hint">Fråga om nätet, till exempel:</p>
                <div class="suggestions">
                  @for (question of suggestions; track question) {
                    <button type="button" class="suggestion" (click)="assistant.ask(question)">
                      {{ question }}
                    </button>
                  }
                </div>
              }
              @for (line of assistant.lines(); track line.id) {
                <div class="line" [class]="line.role">
                  @if (line.card; as card) {
                    <div class="tool">
                      <strong>{{ card.title }}</strong>
                      @for (row of card.rows; track $index) {
                        @if (row.ref; as ref) {
                          <button type="button" class="row" (click)="assistant.openObject(ref)">
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
                      @if (card.more) {
                        <small>{{ card.more }}</small>
                      }
                    </div>
                  }
                  @if (line.change; as change) {
                    <div class="tool change" role="group" aria-label="Ändringsbegäran">
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
                          @if (change.problems.length === 0) {
                            <p>Ska vi skriva det här?</p>
                          } @else {
                            <p>Det här går inte att skriva som det är.</p>
                          }
                          <div class="answers">
                            <button
                              type="button"
                              class="text-button"
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
                          <p class="status"><span class="dot" aria-hidden="true"></span>Skriver</p>
                        }
                        @case ('written') {
                          <p class="status">
                            <span class="dot done" aria-hidden="true"></span>Ändringen är skriven
                          </p>
                        }
                        @case ('declined') {
                          <p class="status">
                            <span class="dot off" aria-hidden="true"></span>Inget skrevs
                          </p>
                        }
                        @case ('failed') {
                          <p class="problem" role="alert">{{ change.message }}</p>
                        }
                      }
                    </div>
                  }
                  <span [attr.role]="line.role === 'error' ? 'alert' : null">{{ line.text }}</span>
                  @if (line.link; as link) {
                    <a [href]="link.href" target="_blank" rel="noopener noreferrer">{{
                      link.label
                    }}</a>
                  }
                </div>
              }
              @if (assistant.busy()) {
                <p class="status"><span class="dot" aria-hidden="true"></span>Lots svarar</p>
              }
            </div>
            <form (submit)="send($event)">
              <textarea
                #input
                rows="2"
                aria-label="Din fråga"
                placeholder="Skriv din fråga"
                [value]="draft()"
                (input)="draft.set(input.value)"
                (keydown.enter)="enter($event)"
              ></textarea>
              <button type="submit" class="text-button" [disabled]="!canSend()">Skicka</button>
            </form>
          }
          @case ('denied') {
            <p class="message" role="alert">
              Du har inte tillgång till Lots. Be en administratör om åtkomst.
            </p>
          }
          @case ('other-person') {
            <p class="message" role="alert">
              Lots svarade för en annan användare än den som är inloggad här, så chatten används
              inte. Logga ut och logga in igen.
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
    }
  `,
  styles: `
    .chat {
      position: fixed;
      right: var(--space-4);
      bottom: var(--space-4);
      z-index: 50;
      display: flex;
      flex-direction: column;
      width: min(380px, calc(100vw - 2 * var(--space-4)));
      height: min(540px, calc(100dvh - 120px));
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-1);
      box-shadow: 0 8px 24px rgb(0 0 0 / 25%);
    }
    /* A call's dialog holds the same corner. */
    .chat.beside-call {
      right: calc(var(--space-4) * 2 + 380px);
    }
    @media (max-width: 820px) {
      .chat.beside-call {
        right: var(--space-4);
        bottom: calc(var(--space-4) + 60px);
      }
    }
    header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      padding: var(--space-2) var(--space-2) var(--space-2) var(--space-3);
      border-bottom: var(--line);
    }
    h2 {
      margin: 0;
      font-size: var(--text-sm);
      font-weight: 600;
    }
    header p {
      margin: 0;
      font-size: var(--text-xs);
      color: var(--text-muted);
    }
    header button {
      border: 0;
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    .log {
      flex: 1;
      overflow-y: auto;
      padding: var(--space-3);
      font-size: var(--text-md);
    }
    .line {
      margin-bottom: var(--space-3);
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    .line.user {
      padding-left: var(--space-2);
      border-left: 2px solid var(--focus);
      font-weight: 600;
    }
    .line.notice,
    .hint {
      font-size: var(--text-sm);
      color: var(--text-muted);
    }
    .line.error {
      font-size: var(--text-sm);
      color: var(--status-conflict);
    }
    .line a {
      display: block;
      margin-top: var(--space-1);
    }
    .hint {
      margin: 0;
    }
    .status {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      margin: 0;
      font-size: var(--text-sm);
      color: var(--text-muted);
    }
    .dot.done {
      background: var(--status-in-service);
    }
    .dot.off {
      background: var(--text-muted);
    }
    .suggestions {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      margin-top: var(--space-2);
    }
    .suggestion {
      padding: var(--space-1) var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: none;
      color: inherit;
      font: inherit;
      font-size: var(--text-sm);
      text-align: left;
      cursor: pointer;
    }
    .tool {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      padding: var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      font-size: var(--text-sm);
      white-space: normal;
    }
    .tool small {
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .row {
      display: flex;
      justify-content: space-between;
      gap: var(--space-2);
      padding: 2px 0;
      border: 0;
      background: none;
      color: inherit;
      font: inherit;
      text-align: left;
    }
    button.row {
      cursor: pointer;
      text-decoration: underline;
      text-decoration-color: var(--text-muted);
    }
    .change {
      border-color: var(--focus);
    }
    .change ul {
      margin: 0;
      padding-left: var(--space-3);
    }
    .change p {
      margin: 0;
    }
    .problem {
      color: var(--status-conflict);
    }
    .answers {
      display: flex;
      gap: var(--space-2);
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--status-construction);
    }
    form {
      display: flex;
      gap: var(--space-2);
      align-items: flex-end;
      padding: var(--space-2);
      border-top: var(--line);
    }
    textarea {
      flex: 1;
      resize: none;
      font: inherit;
      font-size: var(--text-md);
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
  protected readonly call = inject(VoiceCall);
  protected readonly suggestions = [
    'Visa alla siter i Gävle med omnejd',
    'Vilka ärenden är öppna just nu?',
    'Hur många siter finns per region?',
    'Vad får jag se?',
  ];
  protected readonly draft = signal('');
  protected readonly canSend = computed(
    () => this.draft().trim().length > 0 && !this.assistant.busy(),
  );
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  constructor() {
    // The latest line stays in view while an answer grows.
    afterRenderEffect(() => {
      this.assistant.lines();
      this.assistant.busy();
      const log = this.log()?.nativeElement;
      if (log) {
        log.scrollTop = log.scrollHeight;
      }
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
    }
  }
}
