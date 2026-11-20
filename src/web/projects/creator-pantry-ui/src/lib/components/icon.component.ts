import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** The icons `cp-icon` draws. Grown one at a time, by a control that needs one. */
export const CP_ICON_NAMES = ['trash'] as const;
export type CpIconName = (typeof CP_ICON_NAMES)[number];

/**
 * A small line icon for a control whose action is better shown than spelled out — `trash` for removing something.
 *
 * **A control's picture, not a picture.** `1.25em` square — a line drawing reads at a little above the text it stands in for — in `currentColor`, stroked like the shell's sidebar icons, so
 * it takes the size and colour of the button it sits in and follows the theme, the hover and a disabled state with
 * it. A drawing that fills a space is `cp-illustration`; a glyph a feature types is text.
 *
 * **It says nothing.** `aria-hidden` and not focusable: the control it sits in carries the name ("Remove step 2"),
 * as an icon-only `cpButton` must.
 *
 * **Use it rather than an inline `<svg>` in a feature**, which the page contract's `illustration` rule fails.
 */
@Component({
  selector: 'cp-icon',
  standalone: true,
  host: { 'aria-hidden': 'true', '[attr.data-name]': 'name()' },
  templateUrl: './icon.component.html',
  styleUrl: './icon.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpIconComponent {
  readonly name = input.required<CpIconName>();
}
