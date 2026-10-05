import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { ActivePlan } from '../plans/active-plan';
import { ClassificationComponent } from './classification';

const schema = {
  key: 'criticality',
  name: 'Kritikalitet',
  description: '',
  appliesTo: ['site', 'service'],
  levels: [
    { level: 1, name: 'Låg', description: '' },
    { level: 5, name: 'Kritisk', description: '' },
  ],
  criticalFrom: 5,
};

describe('classification of an object (#176)', () => {
  async function setup(url: string) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpTestingController);
    await TestBed.inject(Router).navigateByUrl(url);
    const fixture = TestBed.createComponent(ClassificationComponent);
    fixture.componentRef.setInput('type', 'site');
    fixture.componentRef.setInput('objectId', 12);
    const settle = async () => {
      for (let i = 0; i < 5; i++) {
        await new Promise((resolve) => setTimeout(resolve));
        TestBed.tick();
        fixture.detectChanges();
      }
    };
    await settle();
    return { http, fixture, settle };
  }

  it('shows the level with its name, and writes a change directly outside a plan', async () => {
    const { http, fixture, settle } = await setup('/');
    http.expectOne('/api/classifications/schemas').flush([schema]);
    http
      .expectOne((r) => r.url === '/api/classifications' && r.method === 'GET')
      .flush([
        {
          schema: 'criticality',
          schemaName: 'Kritikalitet',
          level: 5,
          name: 'Kritisk',
          critical: true,
          source: 'set',
          setBy: 'x',
          setAt: '',
        },
      ]);
    await settle();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('5 · Kritisk');
    expect(el.querySelector('[data-critical="true"]')).not.toBeNull();

    const select = el.querySelector('select') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new Event('change'));
    const put = http.expectOne((r) => r.url === '/api/classifications' && r.method === 'PUT');
    expect(put.request.body).toEqual({ type: 'site', id: 12, schema: 'criticality', level: 1 });
    put.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    http.expectOne((r) => r.url === '/api/classifications' && r.method === 'GET').flush([]);
    fixture.destroy();
  });

  it('proposes the change as an operation when a draft plan is open', async () => {
    const { http, fixture, settle } = await setup('/?plan=5');
    http.expectOne('/api/classifications/schemas').flush([schema]);
    http.expectOne((r) => r.url === '/api/classifications' && r.method === 'GET').flush([]);
    http.expectOne('/api/plans/5/view').flush({
      plan: { id: 5, name: 'Klassa om', status: 'draft' },
      changes: [],
      plans: [],
      sites: [],
      extent: null,
      problems: 0,
      elapsedMs: 1,
      planned: null,
    });
    await settle();
    const add = vi.spyOn(TestBed.inject(ActivePlan), 'add').mockResolvedValue({} as never);
    const select = (fixture.nativeElement as HTMLElement).querySelector(
      'select',
    ) as HTMLSelectElement;
    select.value = '5';
    select.dispatchEvent(new Event('change'));
    await settle();
    expect(add).toHaveBeenCalledWith({
      kind: 'set_classification',
      type: 'site',
      objectId: 12,
      schema: 'criticality',
      level: 5,
    });
    fixture.destroy();
  });

  it('says what a derived level comes from (#177)', async () => {
    const { http, fixture, settle } = await setup('/');
    http.expectOne('/api/classifications/schemas').flush([schema]);
    http.expectOne((r) => r.url === '/api/classifications' && r.method === 'GET').flush([]);
    await settle();
    http
      .expectOne((r) => r.url.startsWith('/api/classifications/derived'))
      .flush({
        schema: 'criticality',
        level: 5,
        name: 'Kritisk',
        critical: true,
        inherited: true,
        reasons: [
          { kind: 'contains', subject: { type: 'equipment', id: 3, code: 'SW-1 AX-48' }, level: 5 },
        ],
        services: 4,
        locationLevels: [],
      });
    await settle();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Härledd');
    expect(text).toContain('5 · Kritisk');
    expect(text).toContain('innehåller SW-1 AX-48 (5)');
    fixture.destroy();
  });
});
