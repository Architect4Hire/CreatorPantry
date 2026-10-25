import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'cp-card', standalone: true, template: '<ng-content />',
  host: { '[class.cp-card--interactive]': 'interactive()', '[class.cp-card--flush]': 'flush()' },
  styles: [`
    :host {
      display:block; padding:var(--cp-space-6); border:1px solid var(--cp-border); border-radius:var(--cp-radius-lg);
      background:var(--cp-surface); color:var(--cp-text); box-shadow:var(--cp-shadow-sm);
    }
    /*
     * The card is meant to read as a popped-forward surface in dark mode too — clearly lighter than
     * the page, not blended into it — but a literal white island next to a near-black page was too
     * harsh a jump. This is a warm, muted parchment tone instead: still ~14.5:1 contrast against the
     * dark page (plenty of "pop"), just not stark white. Re-declaring the neutral tokens inside the
     * card's own scope (rather than just flipping background/color) means any projected content that
     * references --cp-text-muted, --cp-border, nested cp-badge/cp-status-pill tones, etc. all resolve
     * correctly against this lighter surface too, instead of inheriting dark-theme values tuned for a
     * dark background. --cp-bg is included too: CpFieldComponent styles inputs/textareas against it
     * for a "recessed" look relative to --cp-surface, so leaving it as the dark page background would
     * strand every form field inside a card as a near-black box floating in a lighter card. Brand/
     * accent tokens (primary, pink, orange, purple, blue, danger) are deliberately left alone so
     * interactive affordances still match the rest of the dark page.
     */
    :host-context([data-cp-theme='dark']) {
      --cp-bg: #ddd3b8; --cp-surface: #eae2cd; --cp-surface-subtle: #e4dbc4; --cp-surface-raised: #cddccd;
      --cp-text: #16231c; --cp-text-muted: #536459; --cp-text-faint: #576357;
      --cp-border: #cbbf9e; --cp-border-strong: #b3a582;
      /* A field on this parchment is a lighter sheet than the card, edged at 3:1 against it. */
      --cp-border-control: #7d725f; --cp-field-bg: #f5efe0;
      /* Primary as *text* on this surface. The brand token is left alone, as above, because it also fills
         buttons and soft chips that must match the dark page; as text it is a bright mint on parchment, about
         1.45:1. This is the same green the light theme uses for text, darkened to clear 4.5:1 here. */
      --cp-primary-text: #146137;
      /* And the focus ring, for the same reason and in the same green. The bright mint reads at 9.97:1 on the
         dark page, but on this parchment it is 1.13:1 to 1.25:1 — a focused control inside a card would have
         had no visible ring at all. This green is 5.04:1 at worst here, and 4.01:1 against a primary button's
         own mint fill, which is the one place in the system where the ring rather than the 2px gap is what
         separates the two. */
      --cp-focus: var(--cp-primary-text);
      /* The soft fill a picked tile, a current step, a success notice or a hovered secondary button sits on. Left
         to the dark page, it was a deep green under this card's dark ink — 1.35:1, so a creator could not read
         the answer they had chosen. This is a light mint the card's own text reads on: ink 12.3:1, muted 4.77:1,
         --cp-primary-text 5.69:1. The bright mint is 1.42:1 on it, which is why nothing writes --cp-primary as
         text on a soft fill; it writes --cp-primary-text, the same colour everywhere outside this card. */
      --cp-primary-soft: #d4e5cf;
    }
    :host.cp-card--flush { padding:0; overflow:hidden; }
    :host.cp-card--interactive { transition:transform var(--cp-duration-fast) var(--cp-ease),border-color var(--cp-duration-fast),box-shadow var(--cp-duration-fast); cursor:pointer; }
    :host.cp-card--interactive:hover { transform:translateY(-2px); border-color:color-mix(in srgb,var(--cp-primary) 45%,var(--cp-border)); box-shadow:var(--cp-shadow-md); }
  `], changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpCardComponent { readonly interactive = input(false); readonly flush = input(false); }
