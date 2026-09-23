import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { MyWorkspaceMembership, decodeMyWorkspaceMemberships } from '../models/auth.models';

export type MyMembershipsState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly memberships: readonly MyWorkspaceMembership[] }
  | { readonly status: 'error' };

/** The typed client for GET /api/v1/me — the signed-in user's own workspace memberships, for the workspace switcher. */
@Injectable({ providedIn: 'root' })
export class WorkspaceMembershipService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private readonly stateSignal = signal<MyMembershipsState>({ status: 'loading' });
  readonly state = this.stateSignal.asReadonly();

  private inFlight: Promise<void> | null = null;
  private loadGeneration = 0;

  /**
   * Loads memberships if not already loaded/loading; safe to call from multiple components. A prior
   * 'error' is retried on the next call rather than cached forever, the same as AuthService.ensureChecked().
   */
  ensureLoaded(): Promise<void> {
    if (this.inFlight) return this.inFlight;
    const status = this.stateSignal().status;
    if (status === 'loading' || status === 'error') return this.load();
    return Promise.resolve();
  }

  /**
   * If a newer call to load() starts before this one's response arrives, this call's result is
   * discarded — the most recently *started* load always wins, regardless of which resolves first.
   */
  async load(): Promise<void> {
    const generation = ++this.loadGeneration;
    const isCurrent = () => generation === this.loadGeneration;

    this.stateSignal.set({ status: 'loading' });

    const request = (async () => {
      const url = this.apiBase.url('/api/v1/me');
      if (!url) {
        if (isCurrent()) this.stateSignal.set({ status: 'error' });
        return;
      }

      try {
        const raw = await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true }));
        if (!isCurrent()) return;
        const memberships = decodeMyWorkspaceMemberships(raw).filter((membership) => membership.status === 'Active');
        this.stateSignal.set({ status: 'ready', memberships });
      } catch {
        if (isCurrent()) this.stateSignal.set({ status: 'error' });
      }
    })();

    this.inFlight = request;
    try {
      await request;
    } finally {
      if (this.inFlight === request) this.inFlight = null;
    }
  }
}
