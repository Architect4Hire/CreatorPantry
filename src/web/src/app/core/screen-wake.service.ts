import { Injectable, signal } from '@angular/core';

/**
 * Keeping the device's screen on while a page is being used hands-free.
 *
 * A service for the reason {@link PrintService} is one: a component asks for the screen to stay on and a test
 * can see that it asked, without a real wake lock, which a browser grants only a visible page on a device
 * that is willing.
 *
 * **It can be refused, and says so.** A device saving battery and a page the browser does not consider active
 * are both refusals, so {@link isRefused} is part of the answer — the caller tells the creator the screen may
 * still dim rather than leaving them to find out at the counter.
 *
 * **The browser takes the lock back whenever the page is hidden**, and does not hand it back by itself. So
 * while a hold is wanted this watches for the page becoming visible again and asks again; otherwise a cook who
 * answered the door would come back to an arrangement that had quietly lapsed. That re-asking is why "wanted"
 * and "held this instant" are different things, and why a caller showing the creator a control should show the
 * former — the lock itself comes and goes with every glance at another tab.
 *
 * **One hold at a time.** The screen belongs to the device, not to a component: a second caller's hold replaces
 * the first, and {@link release} ends whichever is current.
 */
@Injectable({ providedIn: 'root' })
export class ScreenWakeService {
  private readonly refused = signal(false);

  /** Whether the last request for the screen was turned down. A later grant and {@link release} both clear it. */
  readonly isRefused = this.refused.asReadonly();

  private sentinel: WakeLockSentinel | null = null;
  private wanted = false;

  private readonly onVisibilityChange = (): void => {
    // Only on the way back. A hidden page cannot hold the lock, so asking then would be refused — and a
    // refusal is something the creator gets told about, which is not what switching tabs means.
    if (this.wanted && globalThis.document?.visibilityState === 'visible') void this.acquire();
  };

  /** Whether this browser can keep the screen on at all. No in Firefox, and no outside a secure context. */
  get isSupported(): boolean {
    return !!globalThis.navigator?.wakeLock;
  }

  /** Asks for the screen to stay on, and to be asked for again each time the page comes back into view. */
  async hold(): Promise<void> {
    if (!this.wanted) {
      this.wanted = true;
      globalThis.document?.addEventListener('visibilitychange', this.onVisibilityChange);
    }

    await this.acquire();
  }

  /** Lets the screen behave normally again. Safe to call having never held one, which is what leaving a page does. */
  release(): void {
    if (!this.wanted) return;

    this.wanted = false;
    globalThis.document?.removeEventListener('visibilitychange', this.onVisibilityChange);
    this.refused.set(false);

    const sentinel = this.sentinel;
    this.sentinel = null;
    // Nothing to do about a release that fails: the lock is the browser's, and it drops it with the page.
    void sentinel?.release().catch(() => undefined);
  }

  private async acquire(): Promise<void> {
    const wakeLock = globalThis.navigator?.wakeLock;
    if (!wakeLock) {
      this.refused.set(true);
      return;
    }

    // Already held. `released` rather than a stored flag because the browser revokes without being asked.
    if (this.sentinel !== null && !this.sentinel.released) return;

    try {
      const sentinel = await wakeLock.request('screen');

      // Let go while the request was in flight: the later word wins, and the lock just granted is dropped.
      if (!this.wanted) {
        void sentinel.release().catch(() => undefined);
        return;
      }

      this.sentinel = sentinel;
      this.refused.set(false);
    } catch {
      this.sentinel = null;
      this.refused.set(true);
    }
  }
}
