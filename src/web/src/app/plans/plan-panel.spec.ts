import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { MapView } from '../map/map-view';
import { ActivePlan } from './active-plan';
import { PlanDetail, PlanDiff, PlanOperation, PlanSummary } from './plan-model';
import { PlanPanelComponent } from './plan-panel';

const summary = (id: number, name: string, extra: Partial<PlanSummary> = {}): PlanSummary => ({
  id,
  name,
  description: '',
  status: 'draft',
  flag: null,
  createdBy: 'cmdb-demo-full',
  createdAt: '2026-09-30T00:00:00Z',
  updatedAt: '2026-09-30T00:00:00Z',
  appliedBy: null,
  appliedAt: null,
  dependsOn: [],
  operations: 1,
  conflicts: 0,
  createdVia: 'api',
  client: null,
  ...extra,
});

const operation = (
  id: number,
  planId: number,
  summaryText: string,
  problem: string | null = null,
): PlanOperation => ({
  id,
  planId,
  seq: 1,
  kind: 'connect',
  summary: summaryText,
  terminals: [],
  target: null,
  connectionKind: 'patch',
  lifecycle: null,
  name: null,
  problem,
  createdBy: 'cmdb-demo-full',
  createdAt: '2026-09-30T00:00:00Z',
  conflicts: [],
  blocked: false,
});

describe('PlanPanelComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  /** Lets effects, resources and promises run without waiting for requests still open. */
  async function settle(fixture: { detectChanges(): void }) {
    for (let i = 0; i < 5; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      TestBed.tick();
      fixture.detectChanges();
    }
  }

  async function open(url: string) {
    await TestBed.inject(Router).navigateByUrl(url);
    const fixture = TestBed.createComponent(PlanPanelComponent);
    await settle(fixture);
    return fixture;
  }

  function flushActive(plan: PlanSummary, dependency: PlanSummary, changes: PlanOperation[]) {
    const detail: PlanDetail = {
      plan,
      dependencies: [dependency],
      operations: changes.filter((c) => c.planId === plan.id),
    };
    const view: PlanDiff = {
      plan,
      plans: [dependency, plan],
      changes,
      sites: [{ id: 3, x: 500000, y: 6600000 }],
      extent: [500000, 6600000, 500000, 6600000],
      problems: changes.filter((c) => c.problem).length,
      elapsedMs: 4.2,
      planned: null,
    };
    http.expectOne('/api/plans').flush([dependency, plan]);
    http.expectOne(`/api/plans/${plan.id}`).flush(detail);
    http.expectOne(`/api/plans/${plan.id}/view`).flush(view);
  }

  it('shows the active plan with the plans under it, problems per operation, and marks it in the map', async () => {
    const fixture = await open('/?plan=7');
    const stage1 = summary(5, 'Etapp 1');
    const stage2 = summary(7, 'Etapp 2', { dependsOn: [5], flag: 'Beroendet Etapp 1 avbröts.' });
    flushActive(stage2, stage1, [
      operation(10, 5, 'Koppla A till B (patch)'),
      operation(11, 7, 'Koppla C till D (patch)', 'Terminalerna är redan kopplade.'),
    ]);
    await settle(fixture);

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Etapp 2');
    expect(text).toContain('Beroendet Etapp 1 avbröts.');
    expect(text).toContain('Från Etapp 1');
    expect(text).toContain('Koppla C till D (patch)');
    expect(text).toContain('Terminalerna är redan kopplade.');
    expect(TestBed.inject(MapView).highlight()?.points).toEqual([[3, 500000, 6600000]]);
    // Only the plan's own operations can be removed.
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('button.remove').length).toBe(1);
  });

  it('applies after confirmation and tells which plans were flagged', async () => {
    const fixture = await open('/?plan=7');
    const plan = summary(7, 'Etapp 2');
    flushActive(plan, summary(5, 'Etapp 1'), [operation(11, 7, 'Koppla C till D (patch)')]);
    await settle(fixture);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    const apply = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find((b) =>
      b.textContent!.includes('För in i produktion'),
    )!;
    apply.click();
    const req = http.expectOne('/api/plans/7/apply');
    expect(req.request.method).toBe('POST');
    req.flush({
      plan: summary(7, 'Etapp 2', { status: 'applied' }),
      flagged: [summary(9, 'Etapp 3')],
    });
    await settle(fixture);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'Etapp 3 behöver ses över efter införandet.',
    );
  });

  it('shows what the plan does to classifications and asks for a reason when applying would break requirements (#179)', async () => {
    const fixture = await open('/?plan=7');
    const plan = summary(7, 'Etapp 2');
    flushActive(plan, summary(5, 'Etapp 1'), [operation(11, 7, 'Koppla C till D (patch)')]);
    await settle(fixture);
    http.expectOne('/api/plans/7/classification').flush({
      plan: 7,
      introduced: 1,
      elapsedMs: 3,
      findings: [
        {
          site: { type: 'site', id: 12, code: 'AGG-0012', name: 'Lingonåsen' },
          before: 0,
          after: 5,
          afterName: 'Kritisk',
          raised: true,
          because: [{ kind: 'contains', subject: { code: 'SW-NY' }, level: 5 }],
          unmet: [
            {
              rule: 'backup-power',
              requirement: 'Reservkraft i minst 4 timmar',
              actual: null,
              required: 4,
              hint: 'Sätt attributet backupHours på siten till minst 4.',
            },
          ],
          introduced: ['backup-power'],
          suggestions: [
            {
              title: 'Planera reservkraft på AGG-0012: backupHours 4',
              operation: {
                kind: 'set_attributes',
                type: 'site',
                objectId: 12,
                attributes: { backupHours: 4 },
              },
            },
          ],
          alternatives: [
            {
              site: { type: 'site', id: 30, code: 'AGG-0030', name: 'Granåsen' },
              distanceM: 12400,
              freeRackUnits: 24,
              cables: 5,
            },
          ],
        },
      ],
    });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('höjs från 0 till 5');
    expect(root.textContent).toContain('innehåller SW-NY (5)');
    expect(root.textContent).toContain('Reservkraft i minst 4 timmar');
    expect(root.textContent).toContain('Granåsen');
    expect(root.textContent).toMatch(/12[.,]4 km/);
    expect(root.textContent).toContain('Planen gör 1 krav ouppfyllda');

    // Applying is refused with the requirements named; a reason is asked for and sent as the exception.
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const ask = vi.spyOn(window, 'prompt').mockReturnValue('Byggs i nästa etapp.');
    [...root.querySelectorAll('button')]
      .find((b) => b.textContent!.includes('För in i produktion'))!
      .click();
    http.expectOne('/api/plans/7/apply').flush(
      {
        detail: 'Planen gör 1 krav ouppfyllda. För in planen med ett undantag och en motivering.',
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle(fixture);
    expect(ask).toHaveBeenCalled();
    http
      .expectOne('/api/plans/7/apply?exception=Byggs%20i%20n%C3%A4sta%20etapp.')
      .flush({ plan: summary(7, 'Etapp 2', { status: 'applied' }), flagged: [] });
  });

  it('creates a plan on top of the chosen ones and switches to it', async () => {
    const fixture = await open('/');
    http
      .expectOne('/api/plans')
      .flush([summary(5, 'Etapp 1'), summary(6, 'Gammal', { status: 'cancelled' })]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

    // A new plan is a short row, opened from the list; what it builds on is under "more".
    expect(root.querySelector('form')).toBeNull();
    [...root.querySelectorAll('button')].find((b) => b.textContent!.trim() === 'Ny plan')!.click();
    await settle(fixture);
    // Cancelled plans are not offered to build on.
    expect(root.querySelectorAll('fieldset input[type=checkbox]').length).toBe(1);
    (root.querySelector('fieldset input[type=checkbox]') as HTMLInputElement).click();
    const name = root.querySelector('input[name=name]') as HTMLInputElement;
    name.value = 'Etapp 2';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = http.expectOne('/api/plans');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Etapp 2', description: '', dependsOn: [5] });
    req.flush(summary(8, 'Etapp 2', { dependsOn: [5] }));
    await settle(fixture);

    expect(TestBed.inject(ActivePlan).id()).toBe(8);
    expect(TestBed.inject(Router).url).toBe('/?plan=8');
  });

  it('opens on an overview with the next step, and resolving shows what needs attention (#252)', async () => {
    const fixture = await open('/?plan=7');
    const plan = summary(7, 'Etapp 2', { operations: 2 });
    flushActive(plan, summary(5, 'Etapp 1'), [
      operation(11, 7, 'Koppla C till D (patch)', 'Terminalerna är redan kopplade.'),
      { ...operation(12, 7, 'Ny site AGG-NY'), kind: 'create_site' },
    ]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.counts')!.textContent).toContain('1 ny');
    expect(root.querySelector('.counts')!.textContent).toContain('1 kopplad');
    const resolve = [...root.querySelectorAll('.overview button.primary')].find((b) =>
      b.textContent!.includes('Lös konflikter'),
    ) as HTMLButtonElement;
    resolve.click();
    await settle(fixture);
    const rows = root.querySelector('cmdb-plan-changes')!.querySelectorAll('tbody tr');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('Terminalerna är redan kopplade.');
  });

  it('shows the circuits and services the plan breaks in its impact tab (#252)', async () => {
    const fixture = await open('/?plan=7');
    flushActive(summary(7, 'Etapp 2'), summary(5, 'Etapp 1'), [operation(11, 7, 'Koppla ifrån')]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    [...root.querySelectorAll('[role=tab]')]
      .find((t) => t.textContent!.includes('Påverkan'))!
      .dispatchEvent(new Event('click'));
    await settle(fixture);
    http.expectOne('/api/plans/7/impact').flush({
      circuits: [
        {
          circuit: { type: 'circuit', id: 40, code: 'TJ-0040' },
          layer: 'physical',
          before: 'intact',
          after: 'broken',
          services: 2,
        },
      ],
      services: [
        { service: { type: 'service', id: 9, code: 'SKP-9' }, before: 'intact', after: 'broken' },
      ],
      broken: 1,
      hiddenCircuits: 0,
      hiddenServices: 0,
      truncated: false,
      elapsedMs: 3.1,
    });
    await settle(fixture);
    expect(root.textContent).toContain('1 kretsar bryts av planen.');
    expect(root.textContent).toContain('TJ-0040');
    expect(root.textContent).toContain('Bruten');
    [...root.querySelectorAll('cmdb-plan-impact button')]
      .find((b) => b.textContent!.includes('före'))!
      .dispatchEvent(new Event('click'));
    await settle(fixture);
    expect(TestBed.inject(Router).url).toContain('trace:circuit.40.prod');
  });

  it('explains what a plan is when there are none', async () => {
    const fixture = await open('/');
    http.expectOne('/api/plans').flush([]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.empty')!.textContent).toContain('En plan samlar ändringar');
    expect(root.querySelector('.empty button.primary')!.textContent).toContain('Ny plan');
  });
});
