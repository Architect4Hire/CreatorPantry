import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Reusable empty-state recipe: icon, title, optional description, and a projected
 * actions slot. Consumers project `cp-button`s into `[cpEmptyStateActions]`; render
 * the primary action first in markup so DOM order, visual order, and tab order agree.
 * Presentation-only — a consumer that swaps this content dynamically (e.g. a list
 * shell going from loading to empty) owns any `aria-live`/`role="status"` announcement.
 */
@Component({
  selector: 'cp-empty-state', standalone: true,
  template: `<span class="icon" aria-hidden="true">{{icon()}}</span><h3>{{title()}}</h3>@if (description()) { <p>{{description()}}</p> }<div class="actions"><ng-content select="[cpEmptyStateActions]" /></div>`,
  host: { '[attr.data-variant]': 'variant()' },
  styles: [`
    :host { display:flex; flex-direction:column; align-items:center; gap:var(--cp-space-3); padding:var(--cp-space-10) var(--cp-space-8); text-align:center; }
    .icon { display:grid; place-items:center; width:3.25rem; height:3.25rem; border-radius:var(--cp-radius-full); background:var(--cp-surface-subtle); font-size:1.5rem; line-height:1; }
    :host([data-variant='no-results']) .icon { background:color-mix(in srgb, var(--cp-surface-subtle) 70%, var(--cp-border)); }
    h3 { margin:0; color:var(--cp-text); font-family:var(--cp-font-display); font-size:var(--cp-font-size-lg); font-weight:400; }
    p { max-width:26rem; margin:0; color:var(--cp-text-muted); font-size:var(--cp-font-size-sm); line-height:var(--cp-leading-normal); }
    .actions { display:flex; flex-wrap:wrap; align-items:center; justify-content:center; gap:var(--cp-space-3); margin-top:var(--cp-space-2); }
    .actions:empty { display:none; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpEmptyStateComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  readonly icon = input('✦');
  readonly variant = input<'first-use' | 'no-results'>('first-use');
}
