import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';

import { FoodImageService } from './food-image.service';

const PHOTOS_PER_TRIO = 3;

/**
 * Three food photos in a row, each tilted a little so the row reads as a scatter of prints rather than a grid.
 * The photos come from the page's {@link FoodImageService}, so several trios on one page never show the same one.
 *
 * The photos are decorative — the page makes no claim about what they show — so they carry empty alt text and
 * stay out of the accessibility tree.
 */
@Component({
  selector: 'cp-landing-image-trio',
  standalone: true,
  templateUrl: './landing-image-trio.component.html',
  styleUrl: './landing-image-trio.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingImageTrioComponent {
  /** Flips the direction of the tilt, so two trios on one page do not lean the same way. */
  readonly mirror = input(false);

  protected readonly photos = inject(FoodImageService).deal(PHOTOS_PER_TRIO);
}
