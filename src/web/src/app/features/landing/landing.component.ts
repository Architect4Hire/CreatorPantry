import { ChangeDetectionStrategy, Component } from '@angular/core';

import { FoodImageService } from './food-image.service';
import { LandingFinalCtaComponent } from './landing-final-cta.component';
import { LandingFooterComponent } from './landing-footer.component';
import { LandingHeroCollageComponent } from './landing-hero-collage.component';
import { LandingHeroComponent } from './landing-hero.component';
import { LandingHowItWorksComponent } from './landing-how-it-works.component';
import { LandingImageTrioComponent } from './landing-image-trio.component';
import { LandingPricingComponent } from './landing-pricing.component';
import { LandingCompareComponent } from './landing-compare.component';
import { LandingCreditsComponent } from './landing-credits.component';
import { LandingFaqComponent } from './landing-faq.component';
import { LandingPromisesComponent } from './landing-promises.component';
import { CAPTURED_TOUR_PANELS, LandingTourComponent } from './landing-tour.component';
import { LandingTrustComponent } from './landing-trust.component';
import { LandingValuePropsComponent } from './landing-value-props.component';

/**
 * The public '' landing page: L.3–L.7's sections composed in order, with the product tour under the value props,
 * a photo trio after it, another after how-it-works, and then the catalogue's own sections — pricing, the
 * credit menu, the draft-and-final pair and the billing promises — each of which renders only where a
 * deployment publishes the catalogue it reads (LP.6, LP.7, LP.8). `<cp-landing-footer>` sits outside
 * `<main>` on purpose — nesting a `<footer>` inside `<main>` strips its `contentinfo` landmark role.
 */
@Component({
  selector: 'cp-landing',
  standalone: true,
  imports: [
    LandingHeroComponent,
    LandingHeroCollageComponent,
    LandingValuePropsComponent,
    LandingTourComponent,
    LandingPricingComponent,
    LandingCreditsComponent,
    LandingCompareComponent,
    LandingPromisesComponent,
    LandingFaqComponent,
    LandingImageTrioComponent,
    LandingHowItWorksComponent,
    LandingTrustComponent,
    LandingFinalCtaComponent,
    LandingFooterComponent,
  ],
  providers: [FoodImageService],
  templateUrl: './landing.component.html',
  styleUrl: './landing.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingComponent {
  /**
   * A value-prop card links into the tour only when its panel is one the tour has — that is, one
   * `npm run capture:tour` has photographed. A panel waiting on its screenshots leaves its card without a link
   * rather than sending a visitor to a tour stop that is not there.
   */
  protected readonly tourPanels = CAPTURED_TOUR_PANELS;
}
