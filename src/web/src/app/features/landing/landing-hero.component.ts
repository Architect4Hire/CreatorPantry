import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent } from '@creator-pantry/ui';

/**
 * The landing page's opening section: owns the page's single <h1>. Purely presentational — CTAs are
 * fixed to /sign-up and /sign-in (this is what a landing hero is for), only the copy is configurable.
 * The optional [cpLandingHeroMedia] slot collapses to a single column when nothing is projected; see
 * the :has() rule in the stylesheet.
 */
@Component({
  selector: 'cp-landing-hero',
  standalone: true,
  imports: [RouterLink, CpButtonComponent],
  templateUrl: './landing-hero.component.html',
  styleUrl: './landing-hero.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingHeroComponent {
  readonly headline = input('The workspace where your recipes become everything else.');
  readonly subheadline = input(
    'Develop and version recipes, turn them into blog posts, social captions, and newsletters, and ' +
      'publish on your terms — with AI drafting proposals you review, never content it ships on its own.',
  );
  readonly primaryCtaLabel = input('Start free');
  readonly secondaryCtaLabel = input('Sign in');
}
