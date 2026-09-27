import { computed, inject, Injectable, signal } from '@angular/core';
import { InMemoryWebStorage, User, UserManager, WebStorageStateStore } from 'oidc-client-ts';
import { RUNTIME_CONFIG } from '../config';

export const CALLBACK_PATH = '/auth/callback';

/**
 * OIDC session (authorization code + PKCE against Authentik).
 * Tokens live in memory only: nothing about the user is written to web storage. The short-lived
 * PKCE state uses sessionStorage during the redirect and is removed by the callback.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly config = inject(RUNTIME_CONFIG);
  private readonly manager = new UserManager({
    authority: this.config.oidcAuthority,
    client_id: this.config.oidcClientId,
    redirect_uri: `${location.origin}${CALLBACK_PATH}`,
    post_logout_redirect_uri: `${location.origin}/`,
    response_type: 'code',
    scope: 'openid profile email offline_access',
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    automaticSilentRenew: true,
  });

  private readonly current = signal<User | null>(null);

  readonly user = this.current.asReadonly();
  readonly isAuthenticated = computed(() => {
    const user = this.current();
    return user !== null && !user.expired;
  });
  readonly displayName = computed(
    () => this.current()?.profile.name ?? this.current()?.profile.preferred_username ?? '',
  );

  constructor() {
    this.manager.events.addUserLoaded((user) => this.current.set(user));
    this.manager.events.addUserUnloaded(() => this.current.set(null));
    this.manager.events.addSilentRenewError(() => this.current.set(null));
  }

  /**
   * Completes a redirect callback or starts a login. Resolves with the path to show once signed in.
   * When a login redirect is started the promise never resolves; the page is leaving.
   */
  async ensureSignedIn(path: string): Promise<string> {
    if (path.startsWith(CALLBACK_PATH)) {
      const user = await this.manager.signinRedirectCallback();
      this.current.set(user);
      return typeof user.state === 'string' ? user.state : '/';
    }
    await this.manager.signinRedirect({ state: path });
    return new Promise<string>(() => undefined);
  }

  accessToken(): string | null {
    return this.isAuthenticated() ? this.current()!.access_token : null;
  }

  async logout(): Promise<void> {
    const idToken = this.current()?.id_token;
    await this.manager.removeUser();
    await this.manager.signoutRedirect({ id_token_hint: idToken });
  }
}
