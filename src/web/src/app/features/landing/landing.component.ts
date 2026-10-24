import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, effect, inject, viewChild } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

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
 * The public landing page: L.3–L.7's sections composed in order, with the product tour under the value props,
 * a photo trio after it, another after how-it-works, and then the catalogue's own sections — pricing, the
 * credit menu, the draft-and-final pair and the billing promises — each of which renders only where a
 * deployment publishes the catalogue it reads (LP.6, LP.7, LP.8). `<cp-landing-footer>` sits outside
 * `<main>` on purpose — nesting a `<footer>` inside `<main>` strips its `contentinfo` landmark role.
 *
 * It answers at two addresses (LP.10): `''`, and `/pricing`, which is this same page opened at its pricing
 * section. The page carries no navigation of its own: it is read by scrolling, and a bar over or under the
 * hero was tried and taken out again for getting in the way of it.
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

  private readonly pricing = viewChild(LandingPricingComponent);
  private readonly document = inject(DOCUMENT);

  /** Whether the pricing section is on the page at all: it renders only where a catalogue is published. */
  private readonly pricingShowing = computed(() => this.pricing()?.available() ?? false);

  constructor() {
    this.openAt(inject(ActivatedRoute).snapshot.data['scrollTo']);
  }

  /**
   * Opens the page at one of its sections, for an address that names one (`/pricing`).
   *
   * It waits rather than scrolls: the section an address names does not exist when the page first draws, and
   * on a deployment that publishes no catalogue it never will — in which case this quietly does nothing and
   * the address stays an ordinary, working landing page. Reading {@link pricingShowing} is what makes this
   * run again when the catalogue answers; the flag is what keeps it to the arrival, so a reader who has since
   * scrolled away is not dragged back by a later redraw.
   *
   * It brings the section into view without animating, and puts the reader's place in the keyboard order
   * there — which is what a browser does for free when a fragment is in the address and it has nothing to
   * wait for.
   */
  private openAt(section: unknown): void {
    if (typeof section !== 'string' || section.length === 0) return;

    const injector = inject(Injector);
    let arrived = false;

    effect(() => {
      // Read first: this is what runs the effect again once the catalogue has answered and the section the
      // address names exists. On a deployment that publishes none it never will, and this quietly does
      // nothing, which is the whole behaviour that address needs at a deployment with no pricing to show.
      this.pricingShowing();
      if (arrived) return;

      // The section is created in the same pass that made it available, so this waits for the DOM.
      afterNextRender(
        () => {
          const target = this.document.getElementById(section);
          if (arrived || !target) return;

          arrived = true;
          // No animation: there is nothing on screen yet for a slide to explain.
          target.scrollIntoView({ behavior: 'auto', block: 'start' });

          // So the first Tab starts at the section the address named rather than at the top of the page. The
          // attribute stays: `-1` adds no tab stop, and taking it off an element that currently has focus
          // makes it unfocusable again, which hands focus straight back to the body.
          target.setAttribute('tabindex', '-1');
          target.focus({ preventScroll: true });
        },
        { injector },
      );
    });
  }
}
