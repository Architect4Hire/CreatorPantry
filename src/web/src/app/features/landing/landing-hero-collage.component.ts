import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  inject,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { CpThemeService } from '@creator-pantry/ui';

import { HERO_COLLAGE_PHOTOS, foodPhoto } from '../../shared/food-photos';

/**
 * A real capture of an AI Recipe Studio first draft's "Keep this draft?" step — Create recipe from this draft,
 * Discard draft, and the promise that nothing is kept until then — taken from the running product with a sample
 * workspace, in each theme. It shows what the hero's subheadline already says, so it adds nothing a listener lacks.
 */
export const PROPOSAL_CAPTURE = {
  light: '/images/landing/proposal-draft-light.webp',
  dark: '/images/landing/proposal-draft-dark.webp',
  width: 640,
  height: 203,
} as const;

/** Only a fine pointer with motion allowed gets the drift; everything else sees the collage at rest. */
export const PARALLAX_QUERY = '(prefers-reduced-motion: no-preference) and (pointer: fine)';

/**
 * The hero's media: three food photos and a proposal card, layered and turned a little, over colour blocks in the
 * fruit accents. Wholly decorative, so the host is `aria-hidden` and every image has empty alt.
 *
 * The three photos are fixed ({@link HERO_COLLAGE_PHOTOS}) and served as AVIF, WebP and JPEG at 480 and 960 px.
 * Fixed is what lets `index.html` preload the front one by name: an LCP candidate a browser can only discover
 * after the lazy landing chunk has run is a round trip of the first screen thrown away (LP.1 finding 2).
 *
 * Nothing shifts as it loads: the collage reserves its own aspect ratio and each image carries its intrinsic size.
 *
 * **The drift.** While the hero is on screen, the pointer's place in the window is written to `--px` and `--py`
 * (each -1 to 1, once a frame at most) and each layer moves by its own depth in CSS. It is written straight to the
 * host's style, never through a signal, so a moving pointer runs no change detection. Under reduced motion or on a
 * coarse pointer there is no listener at all, and the stylesheet applies no translate either; a preference that
 * changes while the page is open is followed.
 */
@Component({
  selector: 'cp-landing-hero-collage',
  standalone: true,
  templateUrl: './landing-hero-collage.component.html',
  styleUrl: './landing-hero-collage.component.css',
  host: { 'aria-hidden': 'true' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingHeroCollageComponent {
  protected readonly photos = {
    front: foodPhoto(HERO_COLLAGE_PHOTOS.front),
    backLeft: foodPhoto(HERO_COLLAGE_PHOTOS.backLeft),
    backRight: foodPhoto(HERO_COLLAGE_PHOTOS.backRight),
  };

  /**
   * What each photograph is actually drawn at, so the browser picks 480 rather than 960 on a phone. The front
   * one is 46% of a collage that is itself capped at 34rem; the two behind it are 40%. Both stay under 480
   * device pixels on a phone at DPR 2.625 and on a desktop at DPR 2.
   */
  protected readonly FRONT_SIZES = '(min-width: 64rem) 15rem, 41vw';
  protected readonly BACK_SIZES = '(min-width: 64rem) 13rem, 36vw';

  protected readonly capture = PROPOSAL_CAPTURE;
  private readonly theme = inject(CpThemeService);
  protected readonly captureSrc = computed(() => PROPOSAL_CAPTURE[this.theme.resolved()]);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly window = inject(DOCUMENT).defaultView;

  private query: MediaQueryList | null = null;
  private observer: IntersectionObserver | null = null;
  private onScreen = true;
  private listening = false;
  private frame = 0;
  private pointer = { x: 0, y: 0 };

  private readonly follow = () => (this.query?.matches ? this.listen() : this.rest());
  private readonly move = (event: PointerEvent) => {
    if (!this.onScreen) return;
    this.pointer = { x: event.clientX, y: event.clientY };
    if (!this.frame) this.frame = this.window!.requestAnimationFrame(() => this.draw());
  };
  private readonly leave = () => this.place(0, 0);

  constructor() {
    afterNextRender(() => {
      if (!this.window?.matchMedia) return;
      this.query = this.window.matchMedia(PARALLAX_QUERY);
      this.query.addEventListener('change', this.follow);
      this.follow();
    });
    inject(DestroyRef).onDestroy(() => {
      this.query?.removeEventListener('change', this.follow);
      this.rest();
    });
  }

  private listen(): void {
    if (this.listening || !this.window) return;
    this.listening = true;
    this.window.addEventListener('pointermove', this.move, { passive: true });
    this.window.document.documentElement.addEventListener('pointerleave', this.leave);
    if ('IntersectionObserver' in this.window) {
      this.observer = new IntersectionObserver(([entry]) => {
        this.onScreen = entry.isIntersecting;
        if (!this.onScreen) this.place(0, 0);
      });
      this.observer.observe(this.host);
    }
  }

  private rest(): void {
    if (this.listening && this.window) {
      this.window.removeEventListener('pointermove', this.move);
      this.window.document.documentElement.removeEventListener('pointerleave', this.leave);
    }
    this.listening = false;
    this.observer?.disconnect();
    this.observer = null;
    if (this.frame) this.window?.cancelAnimationFrame(this.frame);
    this.frame = 0;
    this.place(0, 0);
  }

  private draw(): void {
    this.frame = 0;
    const win = this.window!;
    const box = this.host.getBoundingClientRect();
    const clamp = (value: number) => Math.max(-1, Math.min(1, value));
    this.place(
      clamp((this.pointer.x - (box.left + box.width / 2)) / (win.innerWidth / 2)),
      clamp((this.pointer.y - (box.top + box.height / 2)) / (win.innerHeight / 2)),
    );
  }

  private place(x: number, y: number): void {
    this.host.style.setProperty('--px', x.toFixed(3));
    this.host.style.setProperty('--py', y.toFixed(3));
  }
}
