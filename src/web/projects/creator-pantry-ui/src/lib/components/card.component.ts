import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'cp-card', standalone: true, template: '<ng-content />',
  host: { '[class.cp-card--interactive]': 'interactive()', '[class.cp-card--flush]': 'flush()' },
  styles: [`
    :host {
      display:block; padding:var(--cp-space-5); border:1px solid var(--cp-border); border-radius:var(--cp-radius-lg);
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
    }
    :host.cp-card--flush { padding:0; overflow:hidden; }
    :host.cp-card--interactive { transition:transform var(--cp-duration-fast) var(--cp-ease),border-color var(--cp-duration-fast),box-shadow var(--cp-duration-fast); cursor:pointer; }
    :host.cp-card--interactive:hover { transform:translateY(-2px); border-color:color-mix(in srgb,var(--cp-primary) 45%,var(--cp-border)); box-shadow:var(--cp-shadow-lg); }
  `], changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpCardComponent { readonly interactive = input(false); readonly flush = input(false); }
