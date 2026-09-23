import { Injectable, inject } from '@angular/core';

import { RuntimeConfigService } from './runtime-config.service';

export type ApiBaseResolution =
  | { readonly status: 'ready'; readonly baseUrl: string }
  | { readonly status: 'unavailable' };

/**
 * The sanctioned indirection over `RuntimeConfigService`. Typed API services (auth, workspace, etc.)
 * inject this to resolve absolute gateway URLs; feature components must never inject
 * `RuntimeConfigService` directly (see .claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class ApiBaseService {
  private readonly runtimeConfig = inject(RuntimeConfigService);

  resolve(): ApiBaseResolution {
    const state = this.runtimeConfig.state();
    return state.status === 'ready' ? { status: 'ready', baseUrl: state.config.gatewayUrl } : { status: 'unavailable' };
  }

  /** Joins `path` onto the resolved gateway origin. Returns null while config is loading or degraded. */
  url(path: string): string | null {
    const resolution = this.resolve();
    return resolution.status === 'ready' ? new URL(path, resolution.baseUrl).toString() : null;
  }
}
