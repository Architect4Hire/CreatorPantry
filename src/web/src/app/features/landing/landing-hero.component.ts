import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent } from '@creator-pantry/ui';

import { CpLogoComponent } from '../../shared/logo/logo.component';

/**
 * The landing page's opening section: owns the page's single <h1>. Purely presentational — CTAs are fixed to
 * /sign-up and /sign-in (this is what a landing hero is for), only the copy is configurable. The landing page
 * projects the collage into [cpLandingHeroMedia]; with nothing projected the hero is one column.
 *
 * `highlight` names the phrase of the headline that carries the hand-drawn underline. It is decoration only: the
 * heading's text is the whole sentence either way, and a phrase the headline does not contain marks nothing.
 *
 * The copy is LP.2's: no claim the product does not back, AI as something that drafts and waits for acceptance,
 * and AI as the Owner's to switch on (`IAiTaskGate`).
 */
@Component({
  selector: 'cp-landing-hero',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpLogoComponent],
  templateUrl: './landing-hero.component.html',
  styleUrl: './landing-hero.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingHeroComponent {
  readonly headline = input('The workspace where your recipes become everything else.');
  readonly highlight = input('everything else.');
  readonly subheadline = input(
    'Write and version your recipes, then turn them into pictures, social posts, blog articles and newsletters. ' +
      'AI drafts what you ask for, and nothing changes until you accept it.',
  );
  readonly note = input('AI is there when you want it. Your workspace’s Owner switches on the features you use.');
  readonly primaryCtaLabel = input('Start free');
  readonly secondaryCtaLabel = input('Sign in');

  protected readonly parts = computed(() => {
    const headline = this.headline();
    const phrase = this.highlight();
    const at = phrase ? headline.lastIndexOf(phrase) : -1;
    return at < 0
      ? { before: headline, marked: '', after: '' }
      : { before: headline.slice(0, at), marked: phrase, after: headline.slice(at + phrase.length) };
  });
}
