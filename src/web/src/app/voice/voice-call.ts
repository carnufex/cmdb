import {
  ChangeDetectionStrategy,
  Component,
  computed,
  CUSTOM_ELEMENTS_SCHEMA,
  inject,
  Injectable,
  InjectionToken,
  signal,
} from '@angular/core';
import { RUNTIME_CONFIG } from '../config';

/** Where the widget gets its config, the same request it makes itself. */
const WIDGET_API = 'https://api.us.elevenlabs.io/v1/convai/agents';

/**
 * The ElevenLabs conversation widget: an exact version in package.json, in a chunk of its own that is loaded only when
 * someone starts a call (#321). ElevenLabs is asked first, because the widget draws nothing at all, and says nothing,
 * when it cannot get its config.
 */
export const WIDGET_LOADER = new InjectionToken<(agentId: string) => Promise<unknown>>(
  'voice widget loader',
  {
    providedIn: 'root',
    factory: () => (agentId) =>
      Promise.all([
        import('@elevenlabs/convai-widget-embed'),
        fetch(`${WIDGET_API}/${agentId}/widget`, { credentials: 'omit' }).then((response) => {
          if (!response.ok) {
            throw new Error(`The agent's widget config answered ${response.status}`);
          }
        }),
      ]),
  },
);

export interface VoiceCallRequest {
  /** Dynamic variables for the agent, e.g. the risk a proactive call is about (#137). */
  variables: Record<string, string>;
  firstMessage?: string;
  label: string;
  /** Which agent answers: the service desk switchboard (default) or the NOC agent directly (ADR-0016). */
  agent?: 'desk' | 'noc';
}

/**
 * Calls to the voice agents from the web app (ADR-0015, ADR-0016): inbound to the service desk switchboard, or a
 * proactive call from the NOC agent about a risk (#137), in the
 * ElevenLabs widget instead of a phone line. The conversation is an external service (like the basemap, ADR-0010): the
 * widget is only loaded when a call starts, and only when an agent is configured.
 */
@Injectable({ providedIn: 'root' })
export class VoiceCall {
  private readonly config = inject(RUNTIME_CONFIG, { optional: true });
  private readonly loader = inject(WIDGET_LOADER);
  private seq = 0;

  readonly agentId = this.config?.voiceAgentId ?? '';
  readonly nocAgentId = this.config?.voiceNocAgentId || this.agentId;
  readonly enabled = this.agentId.length > 0;
  readonly current = signal<(VoiceCallRequest & { key: number; agentId: string }) | null>(null);
  readonly widget = signal<'loading' | 'ready' | 'failed'>('loading');

  start(request: VoiceCallRequest): void {
    if (!this.enabled) {
      return;
    }
    // A new key recreates the widget, so each call starts fresh with its own variables.
    const agentId = request.agent === 'noc' ? this.nocAgentId : this.agentId;
    const key = ++this.seq;
    this.widget.set('loading');
    this.current.set({ ...request, agentId, key });
    const settled = (state: 'ready' | 'failed') => {
      if (this.seq === key) {
        this.widget.set(state);
      }
    };
    this.loader(agentId).then(
      () => settled('ready'),
      () => settled('failed'),
    );
  }

  close(): void {
    this.current.set(null);
  }
}

/** The call in progress: the widget, expanded, with the call's variables and a way to hang up. */
@Component({
  selector: 'cmdb-voice-call',
  changeDetection: ChangeDetectionStrategy.OnPush,
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
  template: `
    @if (call.current(); as c) {
      @for (k of [c.key]; track k) {
        <section class="call" aria-label="Samtal med röstagenten">
          <header>
            <span>{{ c.label }}</span>
            <button type="button" aria-label="Stäng samtalet" (click)="call.close()">×</button>
          </header>
          <div class="widget" [class.loaded]="call.widget() === 'ready'">
            @switch (call.widget()) {
              @case ('ready') {
                <elevenlabs-convai
                  [attr.agent-id]="c.agentId"
                  variant="full"
                  always-expanded="true"
                  show-resize-button="false"
                  [attr.dynamic-variables]="variables()"
                  [attr.override-first-message]="c.firstMessage ?? null"
                ></elevenlabs-convai>
              }
              @case ('failed') {
                <p role="alert">
                  Samtalet kunde inte laddas. Kontrollera anslutningen och att inget i webbläsaren
                  blockerar elevenlabs.io, ladda om sidan och försök igen.
                </p>
              }
              @default {
                <p>Laddar samtalet…</p>
              }
            }
          </div>
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
      width: min(380px, calc(100vw - 2 * var(--space-4)));
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
    .widget {
      position: relative;
      height: min(540px, calc(100dvh - 120px));
    }
    /* The widget's colours come from the agent's config, not our theme. The strip below the sheet holds its credit line. */
    .widget.loaded {
      background: #fff;
    }
    .widget p {
      margin: 0;
      padding: var(--space-3);
      font-size: var(--text-sm);
    }
    /* By default the widget covers the viewport (fixed, inset 0) with its sheet floating in a corner, 80px short of the
       top of that area. Here the sheet belongs in the dialog, so the area is moved up by those 80px. */
    elevenlabs-convai {
      position: absolute;
      inset: -80px 0 20px;
      display: block;
      --el-overlay-padding: 0px;
      --el-sheet-radius: 0px;
    }
  `,
})
export class VoiceCallComponent {
  protected readonly call = inject(VoiceCall);
  protected readonly variables = computed(() =>
    JSON.stringify(this.call.current()?.variables ?? {}),
  );
}
