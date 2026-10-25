import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, inject, viewChild } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { CpLogoComponent } from '../../shared/logo/logo.component';
import { LandingFooterComponent } from './landing-footer.component';
import { LegalDocument, parseLegalDocument } from './legal-document';
import { LegalInlineComponent } from './legal-inline.component';
import { legalPage } from './legal-pages';

/**
 * One of the site's legal documents — the Privacy Policy, the Terms of Service or the Acceptable Use Policy —
 * at its own public address, read from `docs/legal/` (see `legal-document.ts`).
 *
 * A public page, like the landing page it is linked from: it sits outside the shell, is open to anyone whether
 * signed in or not (a signed-in person is held to these documents too), and reads at the landing page's one
 * body size. Its head is the logo, linking home, over the document's title; its foot is the landing page's own
 * footer, so the three documents link to one another from every one of them.
 *
 * It opens at its top with the reader's place there: the footer link that brought them here was at the
 * bottom of the page they came from, and the router keeps the scroll position it found.
 */
@Component({
  selector: 'cp-legal-page',
  standalone: true,
  imports: [RouterLink, CpLogoComponent, LandingFooterComponent, LegalInlineComponent],
  templateUrl: './legal-page.component.html',
  styleUrl: './legal-page.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalPageComponent {
  protected readonly document: LegalDocument = parseLegalDocument(
    legalPage(inject(ActivatedRoute).snapshot.data['legal'])?.markdown ?? '',
  );

  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  constructor() {
    const view = inject(DOCUMENT).defaultView;
    afterNextRender(() => {
      view?.scrollTo({ top: 0, left: 0, behavior: 'instant' });
      this.heading().nativeElement.focus({ preventScroll: true });
    });
  }
}
