import { ChangeDetectionStrategy, Component } from '@angular/core';

import { LandingFinalCtaComponent } from './landing-final-cta.component';
import { LandingFooterComponent } from './landing-footer.component';
import { LandingHeroComponent } from './landing-hero.component';
import { LandingHowItWorksComponent } from './landing-how-it-works.component';
import { LandingTrustComponent } from './landing-trust.component';
import { LandingValuePropsComponent } from './landing-value-props.component';

/**
 * The public '' landing page: L.3–L.7's sections composed in order. `<cp-landing-footer>` sits outside
 * `<main>` on purpose — nesting a `<footer>` inside `<main>` strips its `contentinfo` landmark role.
 */
@Component({
  selector: 'cp-landing',
  standalone: true,
  imports: [
    LandingHeroComponent,
    LandingValuePropsComponent,
    LandingHowItWorksComponent,
    LandingTrustComponent,
    LandingFinalCtaComponent,
    LandingFooterComponent,
  ],
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingComponent {}
