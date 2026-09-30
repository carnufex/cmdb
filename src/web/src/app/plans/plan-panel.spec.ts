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

  it('creates a plan on top of the chosen ones and switches to it', async () => {
    const fixture = await open('/');
    http
      .expectOne('/api/plans')
      .flush([summary(5, 'Etapp 1'), summary(6, 'Gammal', { status: 'cancelled' })]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

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
});
