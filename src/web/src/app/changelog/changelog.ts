import { HttpClient, httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  inject,
  Injectable,
  OnInit,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Auth } from '../auth/auth';
import { Tools } from '../shell/tools';

/** GET /api/changelog (#82) */
export interface ChangelogPost {
  key: string;
  version: string;
  date: string;
  category: 'nytt' | 'förbättrat' | 'rättat';
  title: string;
  body: string;
  action: string | null;
  issues: number[];
  unread: boolean;
}

export interface ChangelogResponse {
  posts: ChangelogPost[];
  unread: number;
}

export const issueUrl = (issue: number) => `https://github.com/carnufex/cmdb/issues/${issue}`;

/** The change log and what the user has not read; opening the panel marks it read. */
@Injectable({ providedIn: 'root' })
export class ChangelogStore {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(Auth);
  private readonly revision = signal(0);

  readonly log = httpResource<ChangelogResponse>(() => {
    this.revision();
    return this.auth.isAuthenticated() ? { url: '/api/changelog' } : undefined;
  });

  async markRead(): Promise<void> {
    if ((this.log.value()?.unread ?? 0) === 0) {
      return;
    }
    await firstValueFrom(this.http.post('/api/changelog/read', null));
    this.revision.update((r) => r + 1);
  }
}

/** What is new (#82): published posts, newest first, the unread ones marked, each linking to its issues. */
@Component({
  selector: 'cmdb-changelog-panel',
  template: `
    <header class="head">
      <h2>Nyheter</h2>
      <button type="button" class="close" aria-label="Stäng nyheter" (click)="tools.close()">
        ×
      </button>
    </header>
    <div class="body">
      @for (p of posts(); track p.key) {
        <article [class.unread]="p.unread">
          <p class="meta">
            <span class="category" [attr.data-category]="p.category">{{ p.category }}</span>
            <time [attr.datetime]="p.date">{{ p.date }}</time>
            @if (p.unread) {
              <span class="new">Ny</span>
            }
          </p>
          <h3>{{ p.title }}</h3>
          <p>{{ p.body }}</p>
          @if (p.action) {
            <p class="action">{{ p.action }}</p>
          }
          <p class="refs">
            @for (i of p.issues; track i) {
              <a [href]="issueUrl(i)" target="_blank" rel="noopener">#{{ i }}</a>
            }
          </p>
        </article>
      } @empty {
        <p class="muted">Inga nyheter ännu.</p>
      }
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      min-height: 36px;
      padding: 0 var(--space-2) 0 var(--space-4);
      border-bottom: var(--line);
      h2 {
        margin: 0;
        font-size: var(--text-md);
        font-weight: 600;
      }
    }
    .close {
      width: 26px;
      height: 26px;
      border: 0;
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: var(--space-3) var(--space-4);
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }
    article {
      border-left: 3px solid transparent;
      padding-left: var(--space-3);
    }
    article.unread {
      border-left-color: var(--action);
    }
    h3 {
      margin: 0 0 var(--space-1);
      font-size: var(--text-md);
    }
    p {
      margin: 0 0 var(--space-1);
      font-size: var(--text-sm);
    }
    .meta {
      display: flex;
      gap: var(--space-2);
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .category {
      text-transform: uppercase;
      letter-spacing: 0.04em;
      font-weight: 600;
    }
    .new {
      color: var(--action);
      font-weight: 600;
    }
    .action {
      font-style: italic;
    }
    .refs a {
      margin-right: var(--space-2);
      font-family: var(--font-mono);
      font-size: var(--text-xs);
    }
    .muted {
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChangelogPanelComponent implements OnInit {
  protected readonly tools = inject(Tools);
  private readonly store = inject(ChangelogStore);
  protected readonly issueUrl = issueUrl;
  /** Kept as first seen, so what was unread stays marked while the panel is open. */
  protected readonly posts = signal<ChangelogPost[]>([]);

  ngOnInit(): void {
    const log = this.store.log.value();
    this.posts.set(log?.posts ?? []);
    void this.store.markRead();
  }
}
