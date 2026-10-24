import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';

import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { COMPARE_ASSETS } from './landing-compare.assets';

/** The quality classes this band compares. The catalogue has to offer both before it claims two tiers. */
export const DRAFT_CLASS = 'draft';
export const FINAL_CLASS = 'high';

/** Where the divider starts: a little past half, so both pictures are plainly there at a glance. */
export const STARTING_REVEAL = 55;

/**
 * "Explore fast. Finish beautifully." (LP.8): the same prompt as a quick draft and as a finished picture, with
 * a divider a reader drags across to compare them, and what each one costs in credits.
 *
 * **It renders only when both pictures exist**, and they do not yet — see {@link COMPARE_ASSETS} for what the
 * product has to be able to do first. The band is built and waiting rather than faked.
 *
 * **The divider is a slider, because that is what it is.** A native `input[type=range]` carries the arrow keys,
 * Home and End, a focus ring and an announced value for nothing, where a dragged `<div>` would have to be given
 * each of those by hand and would still be a div. Under it, the two pictures are a real pair: both are in the
 * page, one clipped to the divider, so a reader who never moves it still sees both — and the text beneath says
 * what the comparison shows for anyone who cannot see either.
 */
@Component({
  selector: 'cp-landing-compare',
  standalone: true,
  templateUrl: './landing-compare.component.html',
  styleUrl: './landing-compare.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingCompareComponent {
  private readonly catalogueService = inject(PlanCatalogueService);

  protected readonly catalogue = toSignal(this.catalogueService.catalogue(), {
    initialValue: PlanCatalogueService.EMPTY,
  });

  protected readonly heading = 'Explore fast. Finish beautifully.';
  protected readonly intro =
    'Try an idea as a quick draft for a few credits, then make the one you love at full quality. Both pictures below came from the same words.';
  protected readonly assets = COMPARE_ASSETS;

  /** How much of the final picture is shown, as a percentage from the left. */
  protected readonly reveal = signal(STARTING_REVEAL);

  protected readonly rates = computed(() => this.catalogue().rates);

  /**
   * Three things are needed, not two. The pictures and the figures are obvious; the third is that the
   * catalogue offers both qualities at all. Committing a pair of images would otherwise put a two-tier claim
   * on the page before the product has two tiers to offer — which is the one thing this band may not do.
   */
  protected readonly renders = computed(() => {
    const classes = this.catalogue().plans.flatMap((plan) => plan.qualityClasses);
    return (
      this.assets !== null &&
      this.rates() !== null &&
      classes.includes(DRAFT_CLASS) &&
      classes.includes(FINAL_CLASS)
    );
  });

  protected formatCredits(credits: number): string {
    return credits.toLocaleString();
  }

  protected revealChanged(event: Event): void {
    this.reveal.set(Number((event.target as HTMLInputElement).value));
  }

  /**
   * How the finished picture is clipped: inset from the **right** by the remainder, so what is shown is
   * `reveal` measured from the left — which is what the slider announces.
   *
   * Written here rather than in the stylesheet so a spec can read it. The first cut clipped from the left,
   * which showed `100 - reveal` of the finished picture while saying `reveal`, and nothing could catch it.
   */
  protected finalClip(): string {
    return `inset(0 ${100 - this.reveal()}% 0 0)`;
  }

  /** What the divider is showing, in words, for the slider's own announcement. */
  protected revealText(): string {
    return `${this.reveal()}% of the finished picture`;
  }
}
