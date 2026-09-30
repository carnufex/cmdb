import { InjectionToken } from '@angular/core';

/** Settings that differ per environment. Served as /config.json (rendered by nginx from env vars). */
export interface RuntimeConfig {
  oidcAuthority: string;
  oidcClientId: string;
  /** Map background: `esri` (default) or `none` for environments without internet access (ADR-0010). */
  basemap?: string;
  /** The voice switchboard (service desk, ADR-0016) in ElevenLabs; empty hides the call buttons. */
  voiceAgentId?: string;
  /** The NOC agent, called directly for a proactive call about a risk (#137); falls back to voiceAgentId. */
  voiceNocAgentId?: string;
}

export const RUNTIME_CONFIG = new InjectionToken<RuntimeConfig>('RUNTIME_CONFIG');

export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  const response = await fetch('/config.json', { cache: 'no-store' });
  if (!response.ok) {
    throw new Error(`Could not load /config.json (${response.status})`);
  }
  return (await response.json()) as RuntimeConfig;
}
