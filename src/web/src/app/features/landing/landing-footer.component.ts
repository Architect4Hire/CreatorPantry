import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * The site's real footer landmark, kept as its own component (split out of the L.7 draft, which had
 * bundled it with the repeat-CTA section) so it can sit outside <main> in LandingComponent — nesting a
 * <footer> inside <main> strips its `contentinfo` ARIA role. The "Sign in" link is real; legal links are
 * visible, native-`disabled` placeholders, never fabricated policy text or dead links that look real.
 */
@Component({
  selector: 'cp-landing-footer',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './landing-footer.component.html',
  styleUrl: './landing-footer.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingFooterComponent {
  readonly productName = input('CreatorPantry');
  readonly legalLinks = input<readonly string[]>(['Privacy Policy', 'Terms of Service']);

  readonly currentYear = new Date().getFullYear();
}
