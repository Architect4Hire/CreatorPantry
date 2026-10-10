import { Injectable } from '@angular/core';

/**
 * Opening the browser's print dialog, which is also how a creator saves a page as a PDF.
 *
 * A service for the reason {@link ClipboardService} is one: a component asks for a print and a test can see
 * that it asked, without a real dialog blocking a headless browser until somebody dismisses it.
 *
 * What gets printed is decided by the page's `@media print` rules, not here. This only opens the dialog.
 */
@Injectable({ providedIn: 'root' })
export class PrintService {
  print(): void {
    globalThis.print?.();
  }
}
