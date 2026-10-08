import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal, Signal } from '@angular/core';

/**
 * Panel images from the catalog (#214) as object URLs. They are fetched through HttpClient so the request carries
 * the sign-in like every other API call, and each file is fetched once per session.
 */
@Injectable({ providedIn: 'root' })
export class CatalogImages {
  private readonly http = inject(HttpClient);
  private readonly urls = new Map<string, Signal<string | null>>();

  /** The image's URL, null until it has loaded or when it could not be. */
  url(file: string): Signal<string | null> {
    let url = this.urls.get(file);
    if (!url) {
      const loaded = signal<string | null>(null);
      this.http
        .get(`/api/catalog/images/${encodeURIComponent(file)}`, { responseType: 'blob' })
        .subscribe({
          next: (blob) => loaded.set(URL.createObjectURL(blob)),
          // Without the picture the ports are still drawn on a plain face; a later open tries again.
          error: () => this.urls.delete(file),
        });
      url = loaded.asReadonly();
      this.urls.set(file, url);
    }
    return url;
  }
}
