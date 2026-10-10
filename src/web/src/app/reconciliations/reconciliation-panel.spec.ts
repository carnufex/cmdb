import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Auth } from '../auth/auth';
import { ActivePlan } from '../plans/active-plan';
import { Tools } from '../shell/tools';
import { ReconciliationReport, ReconciliationSummary } from './reconciliation-model';
import { ReconciliationPanelComponent } from './reconciliation-panel';

describe('reconciliation panel', () => {
  const runs: ReconciliationSummary[] = [
    {
      id: 7,
      source: 'acme-monitor',
      runBy: 'integration-acme-monitor',
      startedAt: '2026-10-10T21:30:00+00:00',
      dryRun: false,
      elapsedMs: 3200,
      reviewPlanId: 12,
      appliedPlanId: null,
      errors: 0,
    },
  ];
  const report: ReconciliationReport = {
    id: 7,
    source: 'acme-monitor',
    dryRun: false,
    counts: [
      {
        objectType: 'equipment',
        reported: 4,
        matched: 2,
        linked: 2,
        new: 1,
        changed: 1,
        unchanged: 1,
        deviations: 1,
        missing: 0,
        outsideScope: 0,
      },
    ],
    operations: 2,
    reasons: { 'not-allowed': 1 },
    deviations: [
      {
        objectType: 'equipment',
        objectId: 5,
        externalId: 'm-d1',
        attribute: 'attributes.firmware',
        source: '2.0.0',
        cmdb: null,
        reason: 'not-allowed',
      },
    ],
    errors: [],
    notReconciled: [],
    reviewPlanId: 12,
    appliedPlanId: null,
    autoApplyProblem: null,
    elapsedMs: 3200,
  };

  const activated: (number | null)[] = [];

  beforeEach(() => {
    activated.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Auth, useValue: { isAuthenticated: () => true } },
        { provide: ActivePlan, useValue: { activate: (id: number | null) => activated.push(id) } },
      ],
    });
  });

  async function settle(fixture: { detectChanges(): void }) {
    for (let i = 0; i < 5; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      TestBed.tick();
      fixture.detectChanges();
    }
  }

  it('lists the runs with their state, opens a report and its plan for review', async () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ReconciliationPanelComponent);
    await settle(fixture);
    http.expectOne('/api/reconciliations').flush(runs);
    await settle(fixture);

    const element = fixture.nativeElement as HTMLElement;
    const run = element.querySelector<HTMLButtonElement>('.runs button')!;
    expect(run.textContent).toContain('acme-monitor');
    expect(run.textContent).toContain('Väntar på granskning');

    run.click();
    await settle(fixture);
    http.expectOne('/api/reconciliations/7').flush(report);
    await settle(fixture);

    expect(element.querySelector('table tbody')!.textContent).toContain('Utrustning');
    const deviation = element.querySelector('.deviations li')!.textContent!;
    expect(deviation).toContain('attributes.firmware');
    expect(deviation).toContain('2.0.0 i källan, – i cmdb');
    expect(deviation).toContain('källan får inte ändra attributet');

    element.querySelector<HTMLButtonElement>('.plans button')!.click();
    expect(activated).toEqual([12]);
    expect(TestBed.inject(Tools).open()).toBe('plans');
    http.verify();
  });
});
