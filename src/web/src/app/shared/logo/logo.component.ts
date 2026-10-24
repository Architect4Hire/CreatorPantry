import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { CpThemeService } from '@creator-pantry/ui';

/** The logo's intrinsic size, given to the image so the browser reserves its box before it loads. */
export const LOGO_WIDTH = 400;
export const LOGO_HEIGHT = 365;

/**
 * The CreatorPantry mark, in the rendition for the resolved theme.
 *
 * The consumer sizes the host (`cp-logo { width: … }`) and the image fills it at its own aspect ratio; there is no
 * style input. `sizes` says how wide the consumer shows it, so the browser picks the 200w or 400w rendition: both
 * are WebP, which is what `index.html` preloads for the landing page's first screen. `priority` is for the one
 * place it is the first thing on screen.
 */
@Component({
  selector: 'cp-logo',
  standalone: true,
  template: `<img
    [src]="base() + '-400.webp'"
    [srcset]="base() + '-200.webp 200w, ' + base() + '-400.webp 400w'"
    [sizes]="sizes()"
    [width]="width"
    [height]="height"
    [attr.fetchpriority]="priority() ? 'high' : null"
    [attr.loading]="priority() ? 'eager' : null"
    decoding="async"
    alt="CreatorPantry"
  />`,
  styles: `
    :host {
      display: block;
    }

    img {
      display: block;
      width: 100%;
      height: auto;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpLogoComponent {
  /** How wide the consumer shows the logo, as an `<img sizes>` value. */
  readonly sizes = input('12.5rem');
  readonly priority = input(false);

  protected readonly width = LOGO_WIDTH;
  protected readonly height = LOGO_HEIGHT;

  private readonly theme = inject(CpThemeService);

  protected readonly base = computed(() => (this.theme.resolved() === 'dark' ? '/images/logodark' : '/images/logo'));
}
