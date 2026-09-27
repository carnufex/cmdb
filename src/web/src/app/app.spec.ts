import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App, Me } from './app';
import { Auth } from './auth/auth';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Auth, useValue: { isAuthenticated: signal(true), logout: () => undefined } },
      ],
    }).compileComponents();
  });

  it('marks the data as synthetic and shows the user the API sees', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const me: Me = {
      username: 'cmdb-demo-region',
      name: 'Demo Region',
      email: null,
      groups: ['cmdb-region-nord'],
    };
    TestBed.inject(HttpTestingController).expectOne('/api/me').flush(me);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.synthetic')?.textContent).toContain('Syntetisk');
    expect(el.querySelector('.user')?.textContent).toContain('Demo Region');
    expect(el.querySelector('.groups')?.textContent).toBe('cmdb-region-nord');
  });
});
