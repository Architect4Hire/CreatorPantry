import { Injectable } from '@angular/core';

/**
 * Handing the creator a plain-text file made in the browser.
 *
 * A service for the reason `ClipboardService` is one: a component asks "did it save?" and a test answers
 * without a real download, which a headless browser would write to disk.
 *
 * **Nothing is fetched.** The text is whatever the caller already holds, so a download never asks the server
 * for anything and never shows a creator words the screen was not showing.
 *
 * **It can fail, and says so** — a browser that refuses object URLs, or a document with no body — so the caller
 * tells the creator the truth rather than announcing a file that never arrived.
 */
@Injectable({ providedIn: 'root' })
export class TextDownloadService {
  save(fileName: string, text: string): boolean {
    try {
      const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
      const link = document.createElement('a');

      link.href = url;
      link.download = fileName;
      link.rel = 'noopener';
      link.hidden = true;
      document.body.appendChild(link);
      link.click();
      link.remove();

      // After the click has been handled: revoking in the same turn cancels the download in some browsers.
      setTimeout(() => URL.revokeObjectURL(url), 0);

      return true;
    } catch {
      return false;
    }
  }
}
