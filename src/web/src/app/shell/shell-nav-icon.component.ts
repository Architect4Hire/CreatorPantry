import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** The sections the shell's navigation draws an icon for, one per sidebar entry. */
export type ShellNavIcon =
  | 'dashboard'
  | 'workflows'
  | 'my-day'
  | 'my-week'
  | 'recipes'
  | 'brand'
  | 'ai'
  | 'image'
  | 'social'
  | 'pictures'
  | 'board'
  | 'prompts'
  | 'settings';

/**
 * One sidebar icon, drawn as a stroked line in the link's own colour so it follows the theme, the hover and the
 * active state with the label beside it. Decorative: the link's text names it, so the consumer hides this from
 * assistive technology.
 *
 * Shell chrome rather than a library part: the icons name this product's sections, which the domain-neutral
 * library must not know about.
 */
@Component({
  selector: 'cp-shell-nav-icon',
  standalone: true,
  template: `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" focusable="false">
    @switch (name()) {
      @case ('dashboard') {
        <rect x="3" y="3" width="7" height="9" rx="1.5" /><rect x="14" y="3" width="7" height="5" rx="1.5" />
        <rect x="14" y="12" width="7" height="9" rx="1.5" /><rect x="3" y="16" width="7" height="5" rx="1.5" />
      }
      @case ('workflows') {
        <circle cx="6" cy="19" r="2.5" /><circle cx="18" cy="5" r="2.5" />
        <path d="M8.5 19h8a3.5 3.5 0 0 0 0-7h-9a3.5 3.5 0 0 1 0-7h8" />
      }
      @case ('my-day') {
        <circle cx="12" cy="12" r="4" />
        <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M6.3 17.7l-1.4 1.4M19.1 4.9l-1.4 1.4" />
      }
      @case ('my-week') {
        <rect x="3" y="4.5" width="18" height="16.5" rx="2" /><path d="M16 2.5v4M8 2.5v4M3 10h18M8 14h.01M12 14h.01M16 14h.01M8 17.5h.01M12 17.5h.01" />
      }
      @case ('recipes') {
        <path d="M2.5 4.5h6a3.5 3.5 0 0 1 3.5 3.5v12.5a2.5 2.5 0 0 0-2.5-2.5h-7z" />
        <path d="M21.5 4.5h-6A3.5 3.5 0 0 0 12 8v12.5a2.5 2.5 0 0 1 2.5-2.5h7z" />
      }
      @case ('brand') {
        <path d="M12 21.5a9.5 9.5 0 1 1 9.5-9.5c0 2.4-1.9 3.5-3.8 3.5h-2.1a1.9 1.9 0 0 0-1.4 3.2 1.7 1.7 0 0 1-1.2 2.8z" />
        <circle cx="7.5" cy="11" r="1" /><circle cx="10.5" cy="7" r="1" /><circle cx="15.5" cy="7.5" r="1" />
      }
      @case ('ai') {
        <path d="M11 3.5l1.7 4.6 4.6 1.7-4.6 1.7L11 16.1l-1.7-4.6-4.6-1.7 4.6-1.7z" />
        <path d="M18.5 14.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z" />
      }
      @case ('image') {
        <rect x="3" y="3" width="18" height="18" rx="2" /><circle cx="9" cy="9" r="2" /><path d="M21 15l-3.1-3.1a2 2 0 0 0-2.8 0L6 21" />
      }
      @case ('social') {
        <path d="M3.5 10.5v3a1 1 0 0 0 1 1h2.5l5 4v-13l-5 4H4.5a1 1 0 0 0-1 1z" />
        <path d="M16 9a4.5 4.5 0 0 1 0 6M18.8 6.2a8.5 8.5 0 0 1 0 11.6" />
      }
      @case ('pictures') {
        <rect x="7" y="7" width="14" height="14" rx="2" /><path d="M3 15.5V5a2 2 0 0 1 2-2h10.5" />
        <circle cx="12" cy="12" r="1.5" /><path d="M21 17l-3-3-6.5 7" />
      }
      @case ('board') {
        <rect x="3" y="3" width="18" height="18" rx="2" /><path d="M8 7.5v7M12 7.5v4M16 7.5v9" />
      }
      @case ('prompts') {
        <path d="M21 15a2 2 0 0 1-2 2H7.5L3 21V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" /><path d="M8 8.5h8M8 12.5h5" />
      }
      @case ('settings') {
        <path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1" />
        <circle cx="15" cy="6" r="2" /><circle cx="9" cy="12" r="2" /><circle cx="17" cy="18" r="2" />
      }
    }
  </svg>`,
  styles: [
    `
      :host {
        display: inline-grid;
        place-items: center;
        flex: none;
      }
      svg {
        display: block;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShellNavIconComponent {
  readonly name = input.required<ShellNavIcon>();
}
