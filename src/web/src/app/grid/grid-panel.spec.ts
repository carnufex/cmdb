import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { GridResult } from './grid-model';
import { GridPanelComponent } from './grid-panel';
import { Selection } from './selection';

const grid: GridResult = {
  kind: 'equipment',
  columns: [{ key: 'firmware', type: 'string', values: null }],
  rows: [1, 2, 3].map((id) => ({
    type: 'equipment' as const,
    id,
    code: `SW-${id}`,
    name: `SW-${id}`,
    lifecycle: 'in_service',
    site: { id: 9, code: 'SKP-9' },
    typeKey: 'acme-ax-24',
    model: 'Acme AX-24',
    attributes: { firmware: '1.0' },
  })),
  truncated: false,
};

describe('GridPanelComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    await TestBed.inject(Router).navigateByUrl('/?plan=7');
  });

  async function settle(fixture: { detectChanges(): void }) {
    for (let i = 0; i < 5; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      TestBed.tick();
      fixture.detectChanges();
    }
  }

  it('loads the selection, pastes a column from Excel and puts the changes into the plan', async () => {
    TestBed.inject(Selection).sites.set({ ids: [9], label: '1 site' });
    const fixture = TestBed.createComponent(GridPanelComponent);
    await settle(fixture);
    const load = http.expectOne('/api/grid');
    expect(load.request.body).toEqual({ kind: 'equipment', siteIds: [9] });
    load.flush(grid);
    http.expectOne('/api/plans/7/view').flush({
      plan: { id: 7, name: 'Firmware', status: 'draft' },
      plans: [],
      changes: [],
      sites: [],
      extent: null,
      problems: 0,
      elapsedMs: 1,
      planned: null,
    });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const firmware = [...root.querySelectorAll('input')].filter((i) =>
      i.getAttribute('aria-label')?.startsWith('firmware'),
    );
    // The viewport renders what fits; the first cell is enough to paste from.
    firmware[0].dispatchEvent(new FocusEvent('focus'));
    const pasteEvent = new Event('paste', { bubbles: true }) as Event & { clipboardData: unknown };
    pasteEvent.clipboardData = { getData: () => '2.0\n2.1\n2.2\n' };
    firmware[0].dispatchEvent(pasteEvent);
    await settle(fixture);

    expect(root.textContent).toContain('3 ändringar');
    [...root.querySelectorAll('button')]
      .find((b) => b.textContent!.includes('Lägg i planen'))!
      .click();
    const batch = http.expectOne('/api/plans/7/operations/batch');
    expect(batch.request.body.operations.map((o: { attributes: unknown }) => o.attributes)).toEqual(
      [{ firmware: '2.0' }, { firmware: '2.1' }, { firmware: '2.2' }],
    );
    batch.flush(
      { detail: 'Operation 2: Attributen passar inte modellens schema: /firmware: fel' },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);

    expect(root.textContent).toContain('SW-2: Attributen passar inte modellens schema');
    expect(root.textContent).toContain('3 ändringar');
  });
});
