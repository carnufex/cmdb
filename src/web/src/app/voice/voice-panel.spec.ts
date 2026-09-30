import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PanelStack } from '../shell/panels';
import { Incident, VoiceActivity, VoicePanelComponent } from './voice-panel';

describe('operations agent panel', () => {
  const incident: Incident = {
    number: 'INC-00001',
    priority: 'P1',
    status: 'open',
    siteId: 12,
    siteCode: 'AGG-0012',
    siteName: 'Lingonåsen',
    reference: 'site:12',
    description: 'Ingen länk på Lingonåsen',
    observations: 'Röd lampa',
    reportedBy: 'Kim Lindqvist (1001)',
    conversationId: 'conv_0123456789abcdef',
    createdAt: '2026-09-30T14:10:00Z',
    enrichment: {
      summary: '1 har reservväg på papperet, men även den går via Lingonåsen (falsk redundans).',
    },
  };
  const activity: VoiceActivity = {
    sms: [
      {
        toPhone: '+46700001001',
        employeeId: '1001',
        body: 'Din kod till Driftagenten: 123456.',
        createdAt: '2026-09-30T14:09:00Z',
      },
    ],
    calls: [
      {
        conversationId: 'conv_1',
        employeeId: '1001',
        tool: 'create_incident',
        outcome: 'P1',
        milliseconds: 14,
        createdAt: '2026-09-30T14:10:00Z',
      },
    ],
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  async function settle(fixture: { detectChanges(): void }) {
    for (let i = 0; i < 5; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      TestBed.tick();
      fixture.detectChanges();
    }
  }

  it('shows incidents with priority as dot and text, the SMS outbox and tool calls, and opens the site', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VoicePanelComponent);
    await settle(fixture);
    http.expectOne((r) => r.url === '/api/incidents').flush([incident]);
    http.expectOne((r) => r.url === '/api/voice/activity').flush(activity);
    await settle(fixture);

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('INC-00001');
    expect(text).toContain('P1 · jour larmad');
    expect(text).toContain('falsk redundans');
    expect(text).toContain('Din kod till Driftagenten: 123456.');
    expect(text).toContain('create_incident');
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-priority="P1"] .dot'),
    ).not.toBeNull();

    const open = vi.spyOn(TestBed.inject(PanelStack), 'open');
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button.link')!.click();
    expect(open).toHaveBeenCalledWith({ type: 'site', id: '12' });
    fixture.destroy();
  });

  it('says why the outbox is missing for a caller without the whole network', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VoicePanelComponent);
    await settle(fixture);
    http.expectOne((r) => r.url === '/api/incidents').flush([]);
    http
      .expectOne((r) => r.url === '/api/voice/activity')
      .flush(null, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Inga ärenden ännu.');
    expect(text).toContain('visas bara med behörighet till hela nätet');
    fixture.destroy();
  });
});
