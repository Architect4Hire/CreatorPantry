import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Credibility, communicated honestly per L.6's restriction: no testimonials, press/brand logos, star
 * ratings, or customer counts — real or fabricated. Mission and audience copy trace to CLAUDE.md's
 * Scope section; stage framing and the no-proof-points-yet line were supplied by the requester rather
 * than invented.
 */
@Component({
  selector: 'cp-landing-trust',
  standalone: true,
  templateUrl: './landing-trust.component.html',
  styleUrl: './landing-trust.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingTrustComponent {
  readonly heading = input('Built for creators, not clicks');
  readonly mission = input(
    'CreatorPantry exists to give food creators a real production workspace — not another feed to perform for.',
  );
  readonly audience = input(
    'Built for food bloggers and content creators who develop recipes and turn them into everything else — ' +
      'blog posts, social captions, newsletters — and want to stay in control of both, not hand it to an ' +
      'algorithm or a stranger’s AI.',
  );
  readonly stage = input(
    'CreatorPantry is in early development. We’re building it in the open, one workspace at a time.',
  );
  readonly proofPointsNote = input(
    'We don’t have customer stories to share yet — when we do, they’ll be real ones, not stock quotes.',
  );
}
