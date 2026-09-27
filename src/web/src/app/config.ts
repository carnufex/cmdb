import { InjectionToken } from '@angular/core';

/** Settings that differ per environment. Served as /config.json (rendered by nginx from env vars). */
export interface RuntimeConfig {
  oidcAuthority: string;
  oidcClientId: string;
  /** Map background: `esri` (default) or `none` for environments without internet access (ADR-0010). */
  basemap?: string;
}

export const RUNTIME_CONFIG = new InjectionToken<RuntimeConfig>('RUNTIME_CONFIG');

export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  const response = await fetch('/config.json', { cache: 'no-store' });
  if (!response.ok) {
    throw new Error(`Could not load /config.json (${response.status})`);
  }
  return (await response.json()) as RuntimeConfig;
}
