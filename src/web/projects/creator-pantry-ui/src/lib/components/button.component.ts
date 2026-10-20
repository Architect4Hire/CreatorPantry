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
    :host { border: 1px solid transparent; border-radius: var(--cp-radius-md); display:inline-flex; align-items:center; justify-content:center; gap:var(--cp-space-2); cursor:pointer; font-weight:600; line-height:1.2; letter-spacing:.005em; text-decoration:none; transition:transform var(--cp-duration-fast) var(--cp-ease), background var(--cp-duration-fast), border-color var(--cp-duration-fast), box-shadow var(--cp-duration-fast), color var(--cp-duration-fast); }
    :host(:active) { transform:translateY(0) scale(.985); }
    :host(:disabled), :host([aria-disabled='true']) { opacity:.52; cursor:not-allowed; pointer-events:none; }
    :host(.cp-button--sm) { min-height:2.5rem; padding:0 var(--cp-space-3); font-size:var(--cp-font-size-sm); }
    :host(.cp-button--md) { min-height:2.75rem; padding:0 var(--cp-space-5); font-size:var(--cp-font-size-control); }
    :host(.cp-button--lg) { min-height:3.125rem; padding:0 var(--cp-space-6); font-size:var(--cp-font-size-md); }
    :host(.cp-button--primary) { background:var(--cp-primary); color:var(--cp-primary-ink); box-shadow:0 1px 2px color-mix(in srgb,var(--cp-primary) 30%,transparent), 0 6px 16px color-mix(in srgb,var(--cp-primary) 20%,transparent); }
    :host(.cp-button--primary:hover) { background:var(--cp-primary-hover); color:var(--cp-primary-ink); transform:translateY(-1px); box-shadow:0 2px 4px color-mix(in srgb,var(--cp-primary) 30%,transparent), 0 10px 24px color-mix(in srgb,var(--cp-primary) 26%,transparent); }
    :host(.cp-button--secondary) { background:var(--cp-surface); color:var(--cp-text); border-color:var(--cp-border-strong); box-shadow:0 1px 2px color-mix(in srgb,var(--cp-text) 6%,transparent); }
    :host(.cp-button--secondary:hover),:host(.cp-button--ghost:hover) { border-color:var(--cp-primary); background:var(--cp-primary-soft); color:var(--cp-primary); }
    :host(.cp-button--ghost) { background:transparent; color:var(--cp-text); border-color:var(--cp-border-strong); }
    :host(.cp-button--text) { background:transparent; color:var(--cp-primary); padding-inline:var(--cp-space-2); }
    :host(.cp-button--text:hover) { text-decoration:underline; }
    :host(.cp-button--danger) { background:var(--cp-danger); color:var(--cp-ink-on-accent); }
    :host(.cp-button--danger:hover) { color:var(--cp-ink-on-accent); filter:brightness(.92); transform:translateY(-1px); }
    :host(.cp-button--full) { width:100%; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpButtonComponent {
  readonly variant = input<CpButtonVariant>('primary');
  readonly size = input<CpButtonSize>('md');
  readonly fullWidth = input(false);
}
