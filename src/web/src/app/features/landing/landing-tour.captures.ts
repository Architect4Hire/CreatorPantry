import type { LandingTourPanel } from './landing-value-props.component';

/**
 * One captured panel: the screenshot files exist for it, in both themes, at 1x and 2x.
 *
 * `width` and `height` are the 1x file's own pixels, so the frame reserves its space before the image arrives.
 */
export interface LandingTourCapture {
  readonly panel: LandingTourPanel;
  readonly width: number;
  readonly height: number;
  /** The day the capture was taken, so a reviewer can see how far behind the product a screenshot has fallen. */
  readonly capturedOn: string;
}

/**
 * **Generated. Do not edit by hand** — `npm run capture:tour` writes this file after it writes the images, and a
 * panel appears here only once its four files are on disk.
 *
 * The tour shows the panels listed here and no others, so an empty list means the tour renders nothing at all
 * rather than a frame of missing pictures. `scripts/capture-tour.mjs` is what fills it.
 */
export const TOUR_CAPTURES: readonly LandingTourCapture[] = [
  { panel: 'ai-recipe-studio', width: 640, height: 400, capturedOn: '2026-10-24' },
  { panel: 'image-studio', width: 640, height: 400, capturedOn: '2026-10-24' },
];

/** Where a panel's screenshot files live. Both themes, both densities, AVIF and WebP, all from this one stem. */
export function tourCaptureStem(panel: LandingTourPanel, theme: 'light' | 'dark'): string {
  return `/images/tour/${panel}-${theme}`;
}
