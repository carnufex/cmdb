import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';
import { finishLotsSignIn, LOTS_CALLBACK_PATH } from './app/assistant/lots-auth';
import { loadRuntimeConfig } from './app/config';

// The sign-in popup for the chat coming back (ADR-0023): it reports to the window that opened it and starts no app.
if (location.pathname === LOTS_CALLBACK_PATH) {
  finishLotsSignIn().catch((err) => console.error(err));
} else {
  loadRuntimeConfig()
    .then((runtime) => bootstrapApplication(App, appConfig(runtime)))
    .catch((err) => console.error(err));
}
