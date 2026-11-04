import { computed, inject, Injectable, InjectionToken, Injector, signal } from '@angular/core';
import { InMemoryWebStorage, User, UserManager, WebStorageStateStore } from 'oidc-client-ts';
import { Auth } from '../auth/auth';
import { RUNTIME_CONFIG, RuntimeConfig } from '../config';

export const LOTS_CALLBACK_PATH = '/auth/lots-callback';

/** The parts of the OIDC client this uses, so a test can stand in for the identity provider. */
export type LotsSignIn = Pick<UserManager, 'signinPopup' | 'signinSilent' | 'removeUser'>;

function settings(config: Pick<RuntimeConfig, 'lotsAuthority' | 'lotsClientId'>) {
  return {
    authority: config.lotsAuthority ?? '',
    client_id: config.lotsClientId ?? '',
    redirect_uri: `${location.origin}${LOTS_CALLBACK_PATH}`,
    popup_redirect_uri: `${location.origin}${LOTS_CALLBACK_PATH}`,
    silent_redirect_uri: `${location.origin}${LOTS_CALLBACK_PATH}`,
    silentRequestTimeoutInSeconds: 8,
    response_type: 'code',
    scope: 'openid profile email offline_access',
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    // Renewed when a question needs it, so an idle tab keeps no session with Lots alive.
    automaticSilentRenew: false,
  };
}

export const LOTS_SIGN_IN = new InjectionToken<LotsSignIn>('Lots sign-in', {
  providedIn: 'root',
  factory: () => new UserManager(settings(inject(RUNTIME_CONFIG))),
});

/**
 * Runs in the hidden frame or the sign-in popup when the identity provider sends it back. Handled before the app starts, since
 * the whole app would start a sign-in of its own there. The window that opened it finishes the code exchange.
 */
export async function finishLotsSignIn(): Promise<void> {
  const manager = new UserManager(
    settings({ lotsAuthority: location.origin, lotsClientId: 'lots' }),
  );
  await (window.parent !== window ? manager.signinSilentCallback() : manager.signinPopupCallback());
}

export type LotsAccess =
  'signed-out' | 'signing-in' | 'signed-in' | 'denied' | 'other-person' | 'failed';

/**
 * The person's own sign-in to Lots (ADR-0023, ADR-0025). Lots accepts only a token issued for Lots, so cmdb's token never
 * leaves cmdb: this is a sign-in of its own, in the identity provider's session the person already has. It happens in a hidden
 * frame, with nothing to click. Without such a session the provider says so, and the person signs in with the popup, which must
 * open from a click or the browser blocks it. Like cmdb's own, the token lives in memory only.
 */
@Injectable({ providedIn: 'root' })
export class LotsAuth {
  private readonly auth = inject(Auth);
  private readonly injector = inject(Injector);
  private readonly current = signal<User | null>(null);
  /** Made at the first sign-in: without a configured Lots there is no client at all. */
  private get manager(): LotsSignIn {
    return this.injector.get(LOTS_SIGN_IN);
  }

  readonly access = signal<LotsAccess>('signed-out');
  readonly signedIn = computed(() => this.access() === 'signed-in');
  /** Who it is at the identity provider, for what is remembered per person. */
  readonly subject = computed(() => this.current()?.profile.sub ?? null);
  readonly displayName = computed(
    () => this.current()?.profile.name ?? this.current()?.profile.preferred_username ?? '',
  );

  /** Signs in without asking anything when the person has a session at the provider. Safe to call again. */
  async connect(): Promise<void> {
    if (this.access() === 'signed-in' || this.access() === 'signing-in') {
      return;
    }
    this.access.set('signing-in');
    try {
      const user = await this.manager.signinSilent();
      if (user) {
        this.accept(user);
      } else {
        this.access.set('signed-out');
      }
    } catch (e: unknown) {
      // No session, or a frame that never answered: the popup is there for that. A refusal is an answer.
      const message = (e as { message?: string }).message ?? '';
      this.access.set(/access_denied|permission|denied/i.test(message) ? 'denied' : 'signed-out');
    }
  }

  /** Opens the popup. Call it directly from a click. */
  async signIn(): Promise<void> {
    try {
      this.accept(await this.manager.signinPopup());
    } catch (e: unknown) {
      this.current.set(null);
      // Someone the provider refuses Lots to, or who closed the window.
      const message = (e as { message?: string }).message ?? '';
      this.access.set(/access_denied|permission|denied/i.test(message) ? 'denied' : 'failed');
    }
  }

  /** A valid token for the question being asked, renewed if it has run out; null when the person must sign in again. */
  async token(): Promise<string | null> {
    const user = this.current();
    if (!user) {
      return null;
    }
    if (!user.expired) {
      return user.access_token;
    }
    try {
      const renewed = await this.manager.signinSilent();
      if (renewed) {
        this.accept(renewed);
        return this.current()?.access_token ?? null;
      }
    } catch {
      // Falls through to signing in again.
    }
    this.current.set(null);
    this.access.set('signed-out');
    return null;
  }

  /** The chat is the person's own: a Lots session for anyone else than the one signed in to cmdb is not used. */
  private accept(user: User): void {
    const same = user.profile.sub === this.auth.user()?.profile.sub;
    this.current.set(same ? user : null);
    this.access.set(same ? 'signed-in' : 'other-person');
    if (!same) {
      void this.manager.removeUser();
    }
  }
}
