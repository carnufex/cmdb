import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';
import { loadRuntimeConfig } from './app/config';

loadRuntimeConfig()
  .then((runtime) => bootstrapApplication(App, appConfig(runtime)))
  .catch((err) => console.error(err));
