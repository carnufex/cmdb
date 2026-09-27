import { HttpClient } from '@angular/common/http';
import { DOCUMENT, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Theme = 'dark' | 'light';

/**
 * Dark is the default; the choice is saved in the user's profile on the server, never in browser
 * storage, so it follows the user and leaves nothing behind on shared machines.
 */
@Injectable({ providedIn: 'root' })
export class ThemeStore {
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  private readonly current = signal<Theme>('dark');

  readonly theme = this.current.asReadonly();

  async load(): Promise<void> {
    try {
      const { theme } = await firstValueFrom(
        this.http.get<{ theme: Theme }>('/api/me/preferences'),
      );
      this.apply(theme);
    } catch {
      this.apply('dark');
    }
  }

  async toggle(): Promise<void> {
    const next: Theme = this.current() === 'dark' ? 'light' : 'dark';
    this.apply(next);
    await firstValueFrom(this.http.put('/api/me/preferences', { theme: next }));
  }

  private apply(theme: Theme): void {
    this.current.set(theme);
    this.document.documentElement.dataset['theme'] = theme;
  }
}
