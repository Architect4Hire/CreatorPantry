import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { CpIllustrationComponent, CpIllustrationName } from './illustration.component';

/**
 * Reusable empty-state recipe: an illustration (or the older text glyph), title, optional description, and a
 * projected actions slot. Consumers project `cp-button`s into `[cpEmptyStateActions]`; render the primary action
 * first in markup so DOM order, visual order, and tab order agree. Presentation-only — a consumer that swaps this
 * content dynamically (e.g. a list shell going from loading to empty) owns any `aria-live`/`role="status"`
 * announcement.
 *
 * `illustration` names one of the drawn set (AU.2.4, D5) and wins over `icon` when both are given. With one, a
 * `first-use` state is the roomy layout — the drawing at full size in the page's tone, the title a step up, the
 * sentence at reading size — that invites the first thing to do; a `no-results` state stays compact, the drawing
 * small. Without one it is the glyph in a disc it has always been, so a page changes only when it asks to.
 */
@Component({
  selector: 'cp-empty-state', standalone: true, imports: [CpIllustrationComponent],
  template: `@if (illustration(); as name) { <cp-illustration class="art" [name]="name" /> } @else { <span class="icon" aria-hidden="true">{{icon()}}</span> }<h3>{{title()}}</h3>@if (description()) { <p>{{description()}}</p> }<div class="actions"><ng-content select="[cpEmptyStateActions]" /></div>`,
  host: { '[attr.data-variant]': 'variant()', '[attr.data-illustrated]': 'illustration() ? "" : null' },
  styles: [`
    :host { display:flex; flex-direction:column; align-items:center; gap:var(--cp-space-3); padding:var(--cp-space-10) var(--cp-space-8); text-align:center; }
    .icon { display:grid; place-items:center; width:3.25rem; height:3.25rem; border-radius:var(--cp-radius-full); background:var(--cp-surface-subtle); font-size:1.5rem; line-height:1; }
    :host([data-variant='no-results']) .icon { background:color-mix(in srgb, var(--cp-surface-subtle) 70%, var(--cp-border)); }
    h3 { margin:0; color:var(--cp-text); font-family:var(--cp-font-display); font-size:var(--cp-font-size-lg); font-weight:400; }
    p { max-width:26rem; margin:0; color:var(--cp-text-muted); font-size:var(--cp-font-size-sm); line-height:var(--cp-leading-normal); }
    .actions { display:flex; flex-wrap:wrap; align-items:center; justify-content:center; gap:var(--cp-space-3); margin-top:var(--cp-space-2); }
    .actions:empty { display:none; }

    /* The drawing takes the page's tone: the primary on an untoned page, the tool's on a toned one. */
    .art { color:var(--cp-tone-text); }
    /* First use, drawn: the roomy layout. The title a step up, the sentence at reading size, a little more air. */
    :host([data-illustrated][data-variant='first-use']) { gap:var(--cp-space-4); padding:var(--cp-space-10) var(--cp-space-6); }
    :host([data-illustrated][data-variant='first-use']) h3 { font-size:var(--cp-font-size-xl); }
    :host([data-illustrated][data-variant='first-use']) p { max-width:30rem; font-size:var(--cp-font-size-md); }
    /* No results stays compact: the drawing small, the layout as it was. */
    :host([data-illustrated][data-variant='no-results']) { padding:var(--cp-space-8) var(--cp-space-6); }
    :host([data-illustrated][data-variant='no-results']) .art { --cp-illustration-size:4.5rem; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpEmptyStateComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  /** The older text glyph, kept for pages not yet drawn; an `illustration` wins over it. */
  readonly icon = input('✦');
  /** One of the drawn set; a `no-results` state uses `search`. */
  readonly illustration = input<CpIllustrationName | null>(null);
  readonly variant = input<'first-use' | 'no-results'>('first-use');
}
