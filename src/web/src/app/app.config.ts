import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { Auth } from './auth/auth';
import { authInterceptor } from './auth/auth-interceptor';
import { RUNTIME_CONFIG, RuntimeConfig } from './config';

export function appConfig(runtime: RuntimeConfig): ApplicationConfig {
  return {
    providers: [
      provideBrowserGlobalErrorListeners(),
      { provide: RUNTIME_CONFIG, useValue: runtime },
      provideHttpClient(withInterceptors([authInterceptor])),
      // Everything requires a signed-in user, so sign in before the router's first navigation.
      provideAppInitializer(async () => {
        const path = await inject(Auth).ensureSignedIn(location.pathname + location.search);
        history.replaceState(null, '', path);
      }),
      provideRouter(routes),
    ],
  };
}
