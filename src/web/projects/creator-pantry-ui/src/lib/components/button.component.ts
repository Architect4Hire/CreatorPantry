import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type CpButtonVariant = 'primary' | 'secondary' | 'ghost' | 'text' | 'danger';
export type CpButtonSize = 'sm' | 'md' | 'lg';

@Component({
  selector: 'button[cpButton], a[cpButton]',
  standalone: true,
  template: '<ng-content />',
  host: {
    '[class]': "'cp-button cp-button--' + variant() + ' cp-button--' + size()",
    '[class.cp-button--full]': 'fullWidth()'
  },
  styles: [`
    :host { border: 1px solid transparent; border-radius: var(--cp-radius-md); display:inline-flex; align-items:center; justify-content:center; gap:var(--cp-space-2); cursor:pointer; font-weight:700; text-decoration:none; transition:transform var(--cp-duration-fast) var(--cp-ease), background var(--cp-duration-fast), border-color var(--cp-duration-fast); }
    :host(:disabled), :host([aria-disabled='true']) { opacity:.52; cursor:not-allowed; pointer-events:none; }
    .cp-button--sm { min-height:2.5rem; padding:0 var(--cp-space-3); font-size:var(--cp-font-size-xs); }
    .cp-button--md { min-height:2.625rem; padding:0 var(--cp-space-4); font-size:var(--cp-font-size-sm); }
    .cp-button--lg { min-height:3rem; padding:0 var(--cp-space-5); font-size:var(--cp-font-size-md); }
    .cp-button--primary { background:var(--cp-primary); color:var(--cp-primary-ink); box-shadow:0 8px 24px color-mix(in srgb,var(--cp-primary) 24%,transparent); }
    .cp-button--primary:hover { background:var(--cp-primary-hover); transform:translateY(-1px); }
    .cp-button--secondary { background:var(--cp-surface-subtle); color:var(--cp-text); border-color:var(--cp-border); }
    .cp-button--secondary:hover,.cp-button--ghost:hover { border-color:var(--cp-primary); color:var(--cp-primary); }
    .cp-button--ghost { background:transparent; color:var(--cp-text); border-color:var(--cp-border); }
    .cp-button--text { background:transparent; color:var(--cp-primary); padding-inline:var(--cp-space-2); }
    .cp-button--text:hover { text-decoration:underline; }
    .cp-button--danger { background:var(--cp-danger); color:var(--cp-ink-on-accent); }
    .cp-button--full { width:100%; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpButtonComponent {
  readonly variant = input<CpButtonVariant>('primary');
  readonly size = input<CpButtonSize>('md');
  readonly fullWidth = input(false);
}
