import { DestroyRef, Injectable, inject, signal } from '@angular/core';

/**
 * Whether the browser says it is online, as a signal, and a way to be told when it comes back.
 *
 * `navigator.onLine` false is reliable — the device has no network at all; true only means it might. So a feature
 * uses this to *explain* a failure ("You're offline") and to retry once the connection returns, never to decide in
 * advance that a request will work.
 */
@Injectable({ providedIn: 'root' })
export class NetworkStatusService {
  private readonly onlineState = signal(typeof navigator === 'undefined' || navigator.onLine !== false);

  readonly online = this.onlineState.asReadonly();

  constructor() {
    if (typeof window === 'undefined') return;

    const update = (): void => this.onlineState.set(navigator.onLine !== false);
    window.addEventListener('online', update);
    window.addEventListener('offline', update);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('online', update);
      window.removeEventListener('offline', update);
    });
  }

  /** Calls `callback` the next time the browser comes back online, once. Returns a function that cancels it. */
  whenOnline(callback: () => void): () => void {
    if (typeof window === 'undefined') return () => undefined;

    const handler = (): void => {
      this.onlineState.set(true);
      callback();
    };
    window.addEventListener('online', handler, { once: true });
    return () => window.removeEventListener('online', handler);
  }
}
