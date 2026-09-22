import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Deployment settings served by CreatorPantry.Web at /runtime-config.json. */
export interface RuntimeConfig {
  readonly gatewayUrl: string;
}

export type RuntimeConfigState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly config: RuntimeConfig }
  | { readonly status: 'error' };

/** Decodes an untrusted response; returns null unless gatewayUrl is an absolute http(s) URL. */
export function decodeRuntimeConfig(value: unknown): RuntimeConfig | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const gatewayUrl = (value as Record<string, unknown>)['gatewayUrl'];
  if (typeof gatewayUrl !== 'string' || !URL.canParse(gatewayUrl)) {
    return null;
  }

  const protocol = new URL(gatewayUrl).protocol;
  return protocol === 'https:' || protocol === 'http:' ? { gatewayUrl } : null;
}

@Injectable({ providedIn: 'root' })
export class RuntimeConfigService {
  private readonly http = inject(HttpClient);
  private readonly stateSignal = signal<RuntimeConfigState>({ status: 'loading' });

  readonly state = this.stateSignal.asReadonly();

  /** Loads the runtime configuration. Failure leaves the app running in a degraded state. */
  async load(): Promise<void> {
    try {
      const config = decodeRuntimeConfig(await firstValueFrom(this.http.get<unknown>('/runtime-config.json')));
      this.stateSignal.set(config ? { status: 'ready', config } : { status: 'error' });
    } catch {
      this.stateSignal.set({ status: 'error' });
    }
  }
}
