import { Injectable } from '@angular/core';

/**
 * Copying text to the creator's clipboard.
 *
 * A service so that a component asks one question — "did it copy?" — and a test can answer it without a real
 * clipboard, which a headless browser refuses without a permission no test should be granting.
 *
 * **It can fail, and says so.** A browser without the async clipboard API, a page that is not focused and a
 * denied permission all end the same way, so the answer is a boolean and the caller tells the creator the truth
 * rather than announcing a copy that did not happen.
 */
@Injectable({ providedIn: 'root' })
export class ClipboardService {
  async copy(text: string): Promise<boolean> {
    try {
      const clipboard = globalThis.navigator?.clipboard;
      if (!clipboard) return false;

      await clipboard.writeText(text);

      return true;
    } catch {
      return false;
    }
  }
}
