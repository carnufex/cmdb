import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RUNTIME_CONFIG } from '../config';
import { MapView } from '../map/map-view';
import { PanelStack } from '../shell/panels';
import { VoiceCall } from './voice-call';
import { Incident, Risk, VoiceActivity, VoicePanelComponent } from './voice-panel';

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

  const risk: Risk = {
    id: 'dig-1-4711',
    kind: 'digging',
    title: 'Schaktning för fjärrvärmeledning korsar kabel K-004711',
    description: 'Markentreprenad Exempel AB gräver 2 oktober–9 oktober.',
    reference: 'cable:4711',
    siteId: 12,
    siteCode: 'AGG-0012',
    siteName: 'Lingonåsen',
    affectedServices: 30,
    criticalServices: 4,
    responsibleEmployeeId: '1001',
    responsibleName: 'Kim Lindqvist',
    suggestedAction: 'Kontakta entreprenören före start.',
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: RUNTIME_CONFIG,
          useValue: { oidcAuthority: '', oidcClientId: '', voiceAgentId: 'agent_test' },
        },
      ],
    });
  });

  function tabButton(fixture: { nativeElement: unknown }, label: string): HTMLButtonElement {
    return [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('[role="tab"]'),
    ].find((b) => b.textContent?.trim() === label)!;
  }

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
    http.expectOne((r) => r.url === '/api/risks').flush([risk]);
    http.expectOne((r) => r.url === '/api/voice/activity').flush(activity);
    await settle(fixture);

    // The map's operations layer follows the live call while the panel is open (#156).
    const site = { id: 12, code: 'AGG-0012', name: 'Lingonåsen', x: 1, y: 2 };
    http
      .expectOne((r) => r.url === '/api/operations/live')
      .flush({
        incidents: [{ number: 'INC-00001', priority: 'P1', site, createdAt: incident.createdAt }],
        live: {
          tool: 'fault_impact',
          reference: 'site:12',
          conversationId: 'conv_1',
          at: incident.createdAt,
          site,
          impact: null,
        },
      });
    http.expectOne((r) => r.url === '/api/operations/works').flush({ works: [], risks: [] });
    await settle(fixture);
    const operations = TestBed.inject(MapView).operations()!;
    expect(operations.live!.site.name).toBe('Lingonåsen');
    expect(operations.incidents.length).toBe(1);

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('INC-00001');
    expect(text).toContain('P1 · jour larmad');
    expect(text).toContain('falsk redundans');
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-priority="P1"] .dot'),
    ).not.toBeNull();

    expect(text).toContain('Schaktning för fjärrvärmeledning korsar kabel K-004711');
    expect(text).toContain('4 kritiska av 30 tjänster');

    // The proactive call carries the risk to the agent, which verifies before any detail.
    const start = vi.spyOn(TestBed.inject(VoiceCall), 'start').mockImplementation(() => undefined);
    const ring = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'),
    ].find((b) => b.textContent?.includes('Ring Kim Lindqvist'))!;
    ring.click();
    expect(start).toHaveBeenCalledWith(
      expect.objectContaining({
        // A risk call goes straight to the NOC agent, not through the service desk (ADR-0016).
        agent: 'noc',
        variables: expect.objectContaining({
          risk_id: 'dig-1-4711',
          responsible_employee_id: '1001',
        }),
      }),
    );
    expect(start.mock.calls[0][0].firstMessage).toContain('verifiera dig');

    const open = vi.spyOn(TestBed.inject(PanelStack), 'open');
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button.link')!.click();
    expect(open).toHaveBeenCalledWith({ type: 'site', id: '12' });

    // The calls tab: tool calls per call, and the outbox.
    tabButton(fixture, 'Samtal').click();
    await settle(fixture);
    const calls = (fixture.nativeElement as HTMLElement).textContent!;
    expect(calls).toContain('create_incident');
    expect(calls).toContain('anst. 1001');
    expect(calls).toContain('Din kod till Driftagenten: 123456.');
    fixture.destroy();
    expect(TestBed.inject(MapView).operations()).toBeNull();
  });

  it('keeps the list short and shows a fresh code above the tabs', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VoicePanelComponent);
    await settle(fixture);
    const many = Array.from({ length: 8 }, (_, n) => ({ ...incident, number: `INC-0000${n + 1}` }));
    http.expectOne((r) => r.url === '/api/incidents').flush(many);
    http.expectOne((r) => r.url === '/api/risks').flush([]);
    http
      .expectOne((r) => r.url === '/api/voice/activity')
      .flush({
        ...activity,
        sms: [
          {
            ...activity.sms[0],
            body: 'Din kod till Driftagenten: 654321.',
            createdAt: new Date().toISOString(),
          },
        ],
      });
    await settle(fixture);

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('section[aria-labelledby="incidents"] details').length).toBe(5);
    expect(el.querySelector('.latest')!.textContent).toContain('654321');
    [...el.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => b.textContent?.includes('Visa alla 8'))!
      .click();
    await settle(fixture);
    expect(el.querySelectorAll('section[aria-labelledby="incidents"] details').length).toBe(8);
    fixture.destroy();
  });

  it('shows an incident impact on the map and resolves it', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VoicePanelComponent);
    await settle(fixture);
    http.expectOne((r) => r.url === '/api/incidents').flush([incident]);
    http.expectOne((r) => r.url === '/api/risks').flush([]);
    http.expectOne((r) => r.url === '/api/voice/activity').flush(activity);
    http.expectOne((r) => r.url === '/api/operations/live').flush({ incidents: [], live: null });
    http.expectOne((r) => r.url === '/api/operations/works').flush({ works: [], risks: [] });
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    // The eye shows this incident's impact on the map (#161).
    el.querySelector<HTMLButtonElement>('button[aria-label="Visa INC-00001 på kartan"]')!.click();
    const route = {
      sites: [{ id: 1, x: 1, y: 2 }],
      cables: [
        {
          id: 2,
          coordinates: [
            [1, 2],
            [3, 4],
          ],
        },
      ],
      extent: [1, 2, 3, 4],
    };
    http.expectOne('/api/incidents/INC-00001/impact').flush({
      priority: 'P1',
      affected: 3,
      servicesDown: 2,
      down: route,
      falseRedundancy: { sites: [], cables: [], extent: [] },
    });
    await settle(fixture);
    expect(
      TestBed.inject(MapView)
        .operations()!
        .incidentImpacts!.map((i) => i.number),
    ).toEqual(['INC-00001']);

    // Resolving it closes it on the server and takes it off the map.
    [...el.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => b.textContent?.trim() === 'Lös')!
      .click();
    const req = http.expectOne('/api/incidents/INC-00001/resolve');
    expect(req.request.method).toBe('POST');
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);
    expect(TestBed.inject(MapView).operations()!.incidentImpacts).toEqual([]);
    fixture.destroy();
  });

  it('says why the outbox is missing for a caller without the whole network', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VoicePanelComponent);
    await settle(fixture);
    http.expectOne((r) => r.url === '/api/incidents').flush([]);
    http.expectOne((r) => r.url === '/api/risks').flush([]);
    http
      .expectOne((r) => r.url === '/api/voice/activity')
      .flush(null, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Inga ärenden ännu.');
    tabButton(fixture, 'Samtal').click();
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'visas bara med behörighet till hela nätet',
    );
    fixture.destroy();
  });
});
