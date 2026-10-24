import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { CpLogoComponent } from '../logo/logo.component';
import { FoodImageService } from '../../features/landing/food-image.service';

/**
 * The frame of a page a visitor meets before they are signed in — sign in, create an account: a food photograph
 * with a line of the product's voice over it, beside one column that holds the page's own form.
 *
 * It sets the scene and nothing else. The form, its heading (the page's one `h1`), its messages and its links are
 * the page's, projected; this draws no heading of its own, so a screen reader meets the page's title first in the
 * main landmark. The photograph is decorative (`alt=""`) and the line over it is ordinary text.
 *
 * Below `56rem` the photograph becomes a short band over the form, so the form stays above the fold on a phone.
 */
@Component({
  selector: 'cp-auth-layout',
  standalone: true,
  imports: [RouterLink, CpLogoComponent],
  providers: [FoodImageService],
  template: `<div class="auth">
    <div class="story">
      <img class="story-photo" [src]="photo" alt="" decoding="async" fetchpriority="low" />
      <div class="story-copy">
        <p class="story-kicker">For food creators</p>
        <p class="story-headline">{{ headline() }}</p>
        @if (dek()) {
          <p class="story-dek">{{ dek() }}</p>
        }
      </div>
    </div>
    <main class="panel">
      <div class="panel-inner">
        <a class="brand" routerLink="/"><cp-logo sizes="6.5rem" /></a>
        <ng-content />
      </div>
    </main>
  </div>`,
  styles: [
    `
      :host {
        display: block;
        min-height: 100vh;
        background: var(--cp-bg);
      }
      .auth {
        display: grid;
        grid-template-columns: minmax(0, 1.05fr) minmax(0, 1fr);
        min-height: 100vh;
      }
      .story {
        position: relative;
        overflow: hidden;
        background: var(--cp-surface-raised);
      }
      .story-photo {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: cover;
      }
      /* The scrim runs from the foot of the photograph, where the words sit, and is gone by the top third: the
         food stays the picture, and the line over it keeps its contrast whatever the photograph holds. */
      .story::after {
        content: '';
        position: absolute;
        inset: 0;
        background: linear-gradient(to top, var(--cp-scrim) 0%, color-mix(in srgb, var(--cp-scrim) 55%, transparent) 38%, transparent 70%);
      }
      .story-copy {
        position: absolute;
        z-index: 1;
        inset: auto 0 0 0;
        display: grid;
        gap: var(--cp-space-3);
        padding: var(--cp-space-10);
        max-width: 38rem;
        color: var(--cp-ink-on-scrim);
      }
      .story-copy p {
        margin: 0;
      }
      .story-kicker {
        font-size: var(--cp-font-size-xs);
        font-weight: 700;
        letter-spacing: var(--cp-tracking-kicker);
        text-transform: uppercase;
        opacity: 0.9;
      }
      .story-headline {
        font: 500 clamp(2rem, 1.4rem + 1.8vw, 3.25rem) / 1.08 var(--cp-font-display);
        letter-spacing: var(--cp-tracking-display);
        text-wrap: balance;
      }
      .story-dek {
        font-size: var(--cp-font-size-lg);
        line-height: 1.5;
        opacity: 0.92;
      }
      .panel {
        display: grid;
        place-items: center;
        padding: var(--cp-space-10) var(--cp-page-gutter);
      }
      .panel-inner {
        display: grid;
        gap: var(--cp-space-8);
        width: min(26rem, 100%);
      }
      .brand {
        justify-self: start;
        display: block;
        border-radius: var(--cp-radius-md);
      }
      .brand cp-logo {
        width: 6.5rem;
      }
      @media (max-width: 56rem) {
        .auth {
          grid-template-columns: minmax(0, 1fr);
          grid-template-rows: auto 1fr;
        }
        .story {
          min-height: 12rem;
        }
        .story-copy {
          padding: var(--cp-space-6) var(--cp-page-gutter);
        }
        .story-dek {
          display: none;
        }
        .panel {
          place-items: start center;
        }
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthLayoutComponent {
  /** One line in the product's voice, set over the photograph. */
  readonly headline = input.required<string>();
  /** A sentence under it. Dropped at a narrow width, where the photograph is a band. */
  readonly dek = input('');

  protected readonly photo = inject(FoodImageService).deal(1)[0];
}
