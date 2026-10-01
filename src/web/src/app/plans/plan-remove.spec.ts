import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { PlanRemoveComponent } from './plan-remove';

describe('removing in a plan (#172)', () => {
  it('adds a removal to the active plan and says why the server refuses one', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpTestingController);
    await TestBed.inject(Router).navigateByUrl('/?plan=5');
    const fixture = TestBed.createComponent(PlanRemoveComponent);
    fixture.componentRef.setInput('type', 'equipment');
    fixture.componentRef.setInput('objectId', 34);
    const settle = async () => {
      for (let i = 0; i < 5; i++) {
        await new Promise((resolve) => setTimeout(resolve));
        TestBed.tick();
        fixture.detectChanges();
      }
    };
    await settle();
    http.expectOne('/api/plans/5/view').flush({
      plan: { id: 5, name: 'Rivning', status: 'draft' },
      changes: [],
      plans: [],
      sites: [],
      extent: null,
      problems: 0,
      elapsedMs: 1,
      planned: null,
    });
    await settle();

    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLButtonElement>('button')!.click();
    const req = http.expectOne('/api/plans/5/operations');
    expect(req.request.body).toEqual({ kind: 'remove', type: 'equipment', objectId: 34 });
    req.flush(
      {
        detail: '2 kretsar går genom det som tas bort. Flytta dem först, annars bryts tjänsterna.',
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();
    expect(el.querySelector('[role="alert"]')!.textContent).toContain('2 kretsar går genom');
    fixture.destroy();
  });
});
