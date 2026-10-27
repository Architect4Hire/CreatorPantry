import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

import { LEGAL_LINKS } from './legal-links';

/** One of the footer's links: its words and the address it opens. */
export interface LandingFooterLink {
  readonly label: string;
  readonly path: string;
}

/**
 * The site's real footer landmark, kept as its own component (split out of the L.7 draft, which had
 * bundled it with the repeat-CTA section) so it can sit outside <main> in LandingComponent — nesting a
 * <footer> inside <main> strips its `contentinfo` ARIA role. The legal pages share it, so each of the three
 * documents links to the other two.
 *
 * Its one navigation is the legal links, named "Legal". The ways into the product are the page's, not the
 * footer's: the empty `<nav aria-label="Footer">` this once rendered was a landmark a screen reader announced
 * and then found nothing in (LP.1 finding 8), and the landmark came back only when there were links to put in it.
 */
@Component({
  selector: 'cp-landing-footer',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './landing-footer.component.html',
  styleUrl: './landing-footer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingFooterComponent {
  readonly productName = input('CreatorPantry');
  readonly legalLinks = input<readonly LandingFooterLink[]>(LEGAL_LINKS);

  readonly currentYear = new Date().getFullYear();
}
