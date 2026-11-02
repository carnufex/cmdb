import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RUNTIME_CONFIG } from '../config';
import { VoiceCall, VoiceCallComponent, WIDGET_LOADER } from './voice-call';

describe('VoiceCallComponent', () => {
  let loads: { agentId: string; resolve: () => void; reject: () => void }[];
  let fixture: ComponentFixture<VoiceCallComponent>;
  let call: VoiceCall;

  beforeEach(() => {
    loads = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: RUNTIME_CONFIG,
          useValue: {
            oidcAuthority: '',
            oidcClientId: '',
            voiceAgentId: 'agent_desk',
            voiceNocAgentId: 'agent_noc',
          },
        },
        {
          provide: WIDGET_LOADER,
          useValue: (agentId: string) =>
            new Promise<void>((resolve, reject) =>
              loads.push({ agentId, resolve, reject: () => reject() }),
            ),
        },
      ],
    });
    fixture = TestBed.createComponent(VoiceCallComponent);
    call = TestBed.inject(VoiceCall);
  });

  const element = () => fixture.nativeElement as HTMLElement;
  const widget = () => element().querySelector('elevenlabs-convai');

  async function settle() {
    await new Promise((resolve) => setTimeout(resolve));
    TestBed.tick();
    fixture.detectChanges();
  }

  it('shows nothing until a call starts and does not load the widget before that', async () => {
    await settle();
    expect(element().querySelector('section')).toBeNull();
    expect(loads).toHaveLength(0);
  });

  it('shows that it is loading, then the widget expanded in the dialog with the call variables', async () => {
    call.start({ variables: { risk_id: 'r1' }, label: 'Samtal med service desk' });
    await settle();
    expect(element().textContent).toContain('Laddar samtalet');
    expect(widget()).toBeNull();

    loads[0].resolve();
    await settle();
    expect(element().textContent).not.toContain('Laddar samtalet');
    expect(widget()?.getAttribute('agent-id')).toBe('agent_desk');
    expect(widget()?.getAttribute('variant')).toBe('full');
    expect(widget()?.getAttribute('always-expanded')).toBe('true');
    expect(JSON.parse(widget()?.getAttribute('dynamic-variables') ?? '')).toEqual({
      risk_id: 'r1',
    });
    expect(widget()?.hasAttribute('override-first-message')).toBe(false);
  });

  it('lets the NOC agent open a proactive call with its own first message', async () => {
    call.start({ agent: 'noc', variables: {}, firstMessage: 'Hej Kim', label: 'Ringer Kim' });
    await settle();
    expect(loads[0].agentId).toBe('agent_noc');
    loads[0].resolve();
    await settle();
    expect(widget()?.getAttribute('agent-id')).toBe('agent_noc');
    expect(widget()?.getAttribute('override-first-message')).toBe('Hej Kim');
  });

  it('asks again for every call, and a slower answer for an earlier call does not count', async () => {
    call.start({ variables: {}, label: 'Samtal med service desk' });
    call.start({ agent: 'noc', variables: {}, label: 'Ringer Kim' });
    loads[0].reject();
    await settle();
    expect(element().querySelector('[role="alert"]')).toBeNull();
    expect(element().textContent).toContain('Laddar samtalet');

    loads[1].resolve();
    await settle();
    expect(widget()?.getAttribute('agent-id')).toBe('agent_noc');
  });

  it('says so when the widget cannot be loaded, and tries again on the next call', async () => {
    call.start({ variables: {}, label: 'Samtal med service desk' });
    loads[0].reject();
    await settle();
    expect(element().querySelector('[role="alert"]')?.textContent).toContain('kunde inte laddas');
    expect(widget()).toBeNull();

    call.start({ variables: {}, label: 'Samtal med service desk' });
    await settle();
    expect(loads).toHaveLength(2);
    loads[1].resolve();
    await settle();
    expect(element().querySelector('[role="alert"]')).toBeNull();
    expect(widget()).not.toBeNull();
  });
});
