import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Auth } from '../auth/auth';
import { ChangelogPanelComponent, ChangelogResponse, ChangelogStore } from './changelog';

describe('change log', () => {
  const log: ChangelogResponse = {
    posts: [
      {
        key: 'b',
        version: 'sha-2',
        date: '2026-09-30',
        category: 'nytt',
        title: 'Planer',
        body: 'Förbered ändringar.',
        action: 'Öppna Planer.',
        issues: [24],
        unread: true,
      },
      {
        key: 'a',
        version: 'sha-1',
        date: '2026-09-29',
        category: 'förbättrat',
        title: 'Snabbare sök',
        body: 'Snabbare.',
        action: null,
        issues: [100],
        unread: false,
      },
    ],
    unread: 1,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Auth, useValue: { isAuthenticated: () => true } },
      ],
    });
  });

  async function settle(fixture?: { detectChanges(): void }) {
    for (let i = 0; i < 5; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      TestBed.tick();
      fixture?.detectChanges();
    }
  }

  it('shows the posts with the unread one marked, links the issues and marks the log read', async () => {
    const http = TestBed.inject(HttpTestingController);
    const store = TestBed.inject(ChangelogStore);
    store.log.value();
    await settle();
    http.expectOne('/api/changelog').flush(log);
    await settle();

    const fixture = TestBed.createComponent(ChangelogPanelComponent);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

    expect([...root.querySelectorAll('h3')].map((h) => h.textContent)).toEqual([
      'Planer',
      'Snabbare sök',
    ]);
    expect(root.querySelectorAll('article.unread').length).toBe(1);
    expect(root.querySelector('a')!.getAttribute('href')).toBe(
      'https://github.com/carnufex/cmdb/issues/24',
    );
    http.expectOne('/api/changelog/read').flush(null);
    await settle(fixture);
    http.expectOne('/api/changelog').flush({ ...log, unread: 0 });
    await settle(fixture);
    expect(store.log.value()!.unread).toBe(0);
    // What was unread stays marked while the panel is open.
    expect(root.querySelectorAll('article.unread').length).toBe(1);
  });
});
