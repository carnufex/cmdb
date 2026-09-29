import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App, Me } from './app';
import { Auth } from './auth/auth';

describe('App', () => {
  let http: HttpTestingController;

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
    http = TestBed.inject(HttpTestingController);
  });

  it('marks the data as synthetic, shows the user and applies the saved theme', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    http.expectOne('/api/me/preferences').flush({ theme: 'light' });
    const me: Me = {
      username: 'cmdb-demo-region',
      name: 'Demo Region',
      email: null,
      groups: ['cmdb-region-nord'],
      scopes: ['Region Nord'],
    };
    http.expectOne('/api/me').flush(me);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('.synthetic')?.textContent).toContain('Syntetisk');
    expect(el.querySelector('.user')?.textContent).toContain('Demo Region');
    expect(el.querySelector('.scope')?.textContent).toContain('Region Nord');
    expect(document.documentElement.dataset['theme']).toBe('light');
  });

  it('saves the theme in the profile when toggled', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    http.expectOne('/api/me/preferences').flush({ theme: 'dark' });
    http.expectOne('/api/me').flush({ username: 'x', name: null, email: null, groups: [] });
    await fixture.whenStable();

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('.icon-button')!
      .click();

    const put = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/me/preferences');
    expect(put.request.body).toEqual({ theme: 'light' });
    expect(document.documentElement.dataset['theme']).toBe('light');
    put.flush({ theme: 'light' });
  });
});
