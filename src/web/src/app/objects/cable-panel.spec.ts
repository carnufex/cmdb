import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { ActivePlan } from '../plans/active-plan';
import { CablePanelComponent, parseNumbers } from './cable-panel';
import { CableDetail } from './models';

const cable: CableDetail = {
  id: 7,
  code: 'K-000007',
  typeName: 'Fiberkabel 12',
  medium: 'fiber',
  conductors: 12,
  conductorsInUse: 4,
  lengthM: 5400,
  lifecycle: 'in_service',
  attributes: {},
  a: { type: 'site', id: 1, code: 'AGG-0001' },
  b: { type: 'site', id: 2, code: 'AGG-0002' },
  circuits: [],
};

describe('inserting a site into a cable (#168)', () => {
  it('reads conductor numbers and ranges', () => {
    expect(parseNumbers('')).toEqual([]);
    expect(parseNumbers('1–4, 7')).toEqual([1, 2, 3, 4, 7]);
    expect(parseNumbers('3-3,1')).toEqual([1, 3]);
    expect(parseNumbers('a')).toBeNull();
    expect(parseNumbers('4-1')).toBeNull();
  });

  it('adds a site at the chosen point and splits the cable in the active plan', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpTestingController);
    await TestBed.inject(Router).navigateByUrl('/?plan=5');
    const fixture = TestBed.createComponent(CablePanelComponent);
    fixture.componentRef.setInput('id', '7');
    const settle = async () => {
      for (let i = 0; i < 5; i++) {
        await new Promise((resolve) => setTimeout(resolve));
        TestBed.tick();
        fixture.detectChanges();
      }
    };
    await settle();
    http.expectOne('/api/cables/7').flush(cable);
    http
      .expectOne((r) => r.url === '/api/plans/5/view')
      .flush({
        plan: { id: 5, name: 'Ny skarvpunkt', status: 'draft' },
        changes: [],
        plans: [],
        sites: [],
        extent: null,
        problems: 0,
        elapsedMs: 1,
        planned: null,
      });
    await settle();
    http.match((r) => r.url.includes('/impact'));

    const add = vi
      .spyOn(TestBed.inject(ActivePlan), 'add')
      .mockResolvedValueOnce({ target: { type: 'site', id: -11, code: 'K-000007-S' } } as never)
      .mockResolvedValueOnce({} as never);
    const el = fixture.nativeElement as HTMLElement;
    const terminate = [...el.querySelectorAll<HTMLInputElement>('.split-form input')].find((i) =>
      i.placeholder.startsWith('Inga'),
    )!;
    terminate.value = '1–2';
    terminate.dispatchEvent(new Event('input'));
    el.querySelector<HTMLFormElement>('.split-form')!.dispatchEvent(new Event('submit'));
    await settle();
    http.expectOne('/api/cables/7/point?at=0.5').flush({ x: 650000, y: 7100000 });
    await settle();

    expect(add).toHaveBeenNthCalledWith(1, {
      kind: 'create_site',
      code: 'K-000007-S',
      name: 'Skarvpunkt på K-000007',
      siteType: 'splice',
      x: 650000,
      y: 7100000,
    });
    expect(add).toHaveBeenNthCalledWith(2, {
      kind: 'split_cable',
      cableId: 7,
      siteId: -11,
      terminate: [1, 2],
    });
    fixture.destroy();
  });
});
