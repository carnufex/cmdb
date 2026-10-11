import { httpResource } from '@angular/common/http';
import { computed, inject, Injectable } from '@angular/core';
import { Auth } from '../auth/auth';

/** The user as the API sees them (GET /api/me). */
export interface Me {
  username: string;
  name: string | null;
  email: string | null;
  groups: string[];
  /** Access scopes that apply (#22), by name. */
  scopes: string[] | null;
}

/** The signed-in user, loaded once, for the toolbar and for what the UI offers (#258). */
@Injectable({ providedIn: 'root' })
export class CurrentUser {
  private readonly auth = inject(Auth);

  readonly me = httpResource<Me>(() => (this.auth.isAuthenticated() ? '/api/me' : undefined));

  /**
   * Whether the user may change the network through plans: the cmdb-full role. The server checks it on every write;
   * the UI only leaves out what would be refused.
   */
  readonly canWrite = computed(() => this.me.value()?.groups.includes('cmdb-full') ?? false);
}
