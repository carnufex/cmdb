import {
  ChangeDetectionStrategy,
  Component,
  computed,
  CUSTOM_ELEMENTS_SCHEMA,
  inject,
  Injectable,
  signal,
} from '@angular/core';
import { RUNTIME_CONFIG } from '../config';

/** The ElevenLabs conversation widget, loaded only when someone starts a call. */
const WIDGET_SCRIPT = 'https://unpkg.com/@elevenlabs/convai-widget-embed';

export interface VoiceCallRequest {
  /** Dynamic variables for the agent, e.g. the risk a proactive call is about (#137). */
  variables: Record<string, string>;
  firstMessage?: string;
  label: string;
}

/**
 * Calls to the operations agent from the web app (ADR-0015): inbound, or a proactive call about a risk (#137), in the
 * ElevenLabs widget instead of a phone line. The widget is an external service (like the basemap, ADR-0010): its
 * script is only fetched when a call starts, and only when an agent is configured.
 */
@Injectable({ providedIn: 'root' })
export class VoiceCall {
  private readonly config = inject(RUNTIME_CONFIG, { optional: true });
  private seq = 0;

  readonly agentId = this.config?.voiceAgentId ?? '';
  readonly enabled = this.agentId.length > 0;
  readonly current = signal<(VoiceCallRequest & { key: number }) | null>(null);

  start(request: VoiceCallRequest): void {
    if (!this.enabled) {
      return;
    }
    loadWidget();
    // A new key recreates the widget, so each call starts fresh with its own variables.
    this.current.set({ ...request, key: ++this.seq });
  }

  close(): void {
    this.current.set(null);
  }
}

let loaded = false;

function loadWidget(): void {
  if (loaded) {
    return;
  }
  loaded = true;
  const script = document.createElement('script');
  script.src = WIDGET_SCRIPT;
  script.async = true;
  document.head.appendChild(script);
}

/** The call in progress: the widget, expanded, with the call's variables and a way to hang up. */
@Component({
  selector: 'cmdb-voice-call',
  changeDetection: ChangeDetectionStrategy.OnPush,
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
  template: `
    @if (call.current(); as c) {
      @for (k of [c.key]; track k) {
        <section class="call" aria-label="Samtal med driftagenten">
          <header>
            <span>{{ c.label }}</span>
            <button type="button" aria-label="Stäng samtalet" (click)="call.close()">×</button>
          </header>
          <elevenlabs-convai
            [attr.agent-id]="call.agentId"
            variant="expanded"
            [attr.dynamic-variables]="variables()"
            [attr.override-first-message]="c.firstMessage ?? null"
            action-text="Ring driftagenten"
            start-call-text="Starta samtal"
            end-call-text="Lägg på"
            listening-text="Lyssnar…"
            speaking-text="Driftagenten talar"
          ></elevenlabs-convai>
        </section>
      }
    }
  `,
  styles: `
    .call {
      position: fixed;
      right: var(--space-4);
      bottom: var(--space-4);
      z-index: 50;
      width: 380px;
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-1);
      box-shadow: 0 8px 24px rgb(0 0 0 / 25%);
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: var(--space-1) var(--space-2) var(--space-1) var(--space-3);
      border-bottom: var(--line);
      font-size: var(--text-sm);
      font-weight: 600;
    }
    header button {
      border: 0;
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    elevenlabs-convai {
      display: block;
      min-height: 480px;
    }
  `,
})
export class VoiceCallComponent {
  protected readonly call = inject(VoiceCall);
  protected readonly variables = computed(() =>
    JSON.stringify(this.call.current()?.variables ?? {}),
  );
}
