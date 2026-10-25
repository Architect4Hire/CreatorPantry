import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** The drawn set (AU.2.4, D5). Each names what it shows, not where it is used. One list, so the type cannot drift. */
export const CP_ILLUSTRATION_NAMES = [
  'recipe', 'picture', 'post', 'article', 'calendar', 'board', 'prompt', 'brand', 'search', 'sparkle',
] as const;

export type CpIllustrationName = (typeof CP_ILLUSTRATION_NAMES)[number];

/**
 * A small line drawing for an empty state, or for a card with no picture of its own (AU.2.4, D5): a recipe card
 * and a bowl, a plated dish under steam, a phone and a speech bubble, a page and a pen, a calendar, a board, a quote,
 * a chef's hat, a magnifier, sparkles.
 *
 * **Drawn for this library.** Not stock, not traced, not made by a model, and no drawing of food carries a claim:
 * a plate is a plate. One colour, `currentColor`, for the lines, a faint wash behind them and the few filled
 * shapes — so it takes whatever colour it sits in, and on a toned page that is `--cp-tone-text`.
 *
 * **Decorative.** `aria-hidden` and not focusable: the title and sentence beside it say everything it shows.
 *
 * Its size is `--cp-illustration-size` (7rem unless the host sets it), scaled from a 120-unit square.
 */
@Component({
  selector: 'cp-illustration',
  standalone: true,
  host: { 'aria-hidden': 'true', '[attr.data-name]': 'name()' },
  template: `
    <svg viewBox="0 0 120 120" focusable="false" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round">
      <circle class="wash" cx="60" cy="60" r="54" />
      @switch (name()) {
        @case ('recipe') {
          <rect x="18" y="20" width="50" height="70" rx="7" />
          <path d="M28 34h24" stroke-width="4" />
          <path d="M28 46h30M28 55h22M28 64h27M28 73h16" />
          <path class="fill" d="M70 82h38a19 19 0 0 1-38 0z" />
          <path d="M100 46l-9 30" />
          <ellipse cx="101.5" cy="41" rx="4" ry="6" transform="rotate(16 101.5 41)" />
        }
        @case ('picture') {
          <rect x="16" y="24" width="88" height="70" rx="9" />
          <path d="M30 76h60" />
          <ellipse class="fill" cx="60" cy="76" rx="26" ry="7" />
          <path d="M42 74a18 14 0 0 1 36 0" />
          <path d="M52 52c-4-5 4-8 0-13M62 50c-4-5 4-8 0-13M72 52c-4-5 4-8 0-13" />
        }
        @case ('post') {
          <rect x="62" y="16" width="38" height="84" rx="8" />
          <path d="M76 25h10" />
          <path d="M70 46h22M70 55h16M70 64h20M70 73h12" />
          <path class="fill" d="M14 30a10 10 0 0 1 10-10h22a10 10 0 0 1 10 10v20a10 10 0 0 1-10 10H34l-10 10v-10a10 10 0 0 1-10-10z" />
          <path d="M35 49c-6-4-9-7-9-11a4.5 4.5 0 0 1 9-2a4.5 4.5 0 0 1 9 2c0 4-3 7-9 11z" />
        }
        @case ('article') {
          <path d="M24 18h40l16 16v62a6 6 0 0 1-6 6H24a6 6 0 0 1-6-6V24a6 6 0 0 1 6-6z" />
          <path d="M64 18v16h16" />
          <path d="M30 42h28" stroke-width="4" />
          <path d="M30 55h38M30 64h38M30 73h30M30 82h34" />
          <path class="fill" d="M88 98l14-28 7 3.5-14 28-8 3z" />
          <path d="M99 76l7 3.5" />
        }
        @case ('calendar') {
          <rect x="16" y="24" width="88" height="74" rx="9" />
          <path d="M16 44h88" />
          <path d="M38 16v16M82 16v16" />
          <path d="M30 58h8M48 58h8M66 58h8M84 58h8M30 72h8M48 72h8M30 86h8M48 86h8" />
          <circle class="fill" cx="76" cy="79" r="12" />
          <path d="M70 79l4.5 4.5 8-9" />
        }
        @case ('board') {
          <rect x="12" y="22" width="28" height="76" rx="6" />
          <rect x="46" y="22" width="28" height="76" rx="6" />
          <rect x="80" y="22" width="28" height="76" rx="6" />
          <rect class="fill" x="17" y="30" width="18" height="14" rx="3" />
          <rect class="fill" x="17" y="50" width="18" height="14" rx="3" />
          <rect class="fill" x="51" y="30" width="18" height="14" rx="3" />
          <rect class="fill" x="85" y="30" width="18" height="14" rx="3" />
          <rect class="fill" x="85" y="50" width="18" height="14" rx="3" />
          <rect class="fill" x="85" y="70" width="18" height="14" rx="3" />
        }
        @case ('prompt') {
          <rect x="12" y="30" width="80" height="58" rx="11" />
          <circle class="solid" cx="30" cy="52" r="5" />
          <path d="M35 52c0 7-3 10-8 12" />
          <circle class="solid" cx="45" cy="52" r="5" />
          <path d="M50 52c0 7-3 10-8 12" />
          <path d="M58 50h24M58 60h18M28 74h46" />
          <path class="fill" d="M100 14l3.5 8.5 8.5 3.5-8.5 3.5-3.5 8.5-3.5-8.5-8.5-3.5 8.5-3.5z" />
        }
        @case ('brand') {
          <path class="fill" d="M38 66c-12 0-17-16-7-23 2-14 21-19 30-9 10-10 30-5 30 9 10 7 5 23-7 23z" />
          <rect x="38" y="66" width="46" height="22" rx="4" />
          <path d="M61 82c-5-3.5-8-6-8-9.5a4 4 0 0 1 8-1.5a4 4 0 0 1 8 1.5c0 3.5-3 6-8 9.5z" />
        }
        @case ('search') {
          <circle class="fill" cx="50" cy="50" r="28" />
          <path d="M71 71l26 26" stroke-width="7" />
          <path d="M38 44h24M38 54h16" />
        }
        @case ('sparkle') {
          <path class="fill" d="M52 26l8 22 22 8-22 8-8 22-8-22-22-8 22-8z" />
          <path d="M92 18l4 10 10 4-10 4-4 10-4-10-10-4 10-4z" />
          <path d="M90 76l3 7 7 3-7 3-3 7-3-7-7-3 7-3z" />
        }
      }
    </svg>
  `,
  styles: [
    `
      :host {
        display: inline-block;
        flex: none;
        width: var(--cp-illustration-size, 7rem);
        height: var(--cp-illustration-size, 7rem);
      }
      svg {
        display: block;
        width: 100%;
        height: 100%;
        overflow: visible;
      }
      .wash {
        fill: currentColor;
        fill-opacity: 0.08;
        stroke: none;
      }
      .fill {
        fill: currentColor;
        fill-opacity: 0.16;
      }
      .solid {
        fill: currentColor;
      }
      /* Forced colours keep the lines; the wash and the tints would only be noise there. */
      @media (forced-colors: active) {
        .wash {
          display: none;
        }
        .fill {
          fill: none;
        }
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpIllustrationComponent {
  /** Which drawing. */
  readonly name = input.required<CpIllustrationName>();
}
