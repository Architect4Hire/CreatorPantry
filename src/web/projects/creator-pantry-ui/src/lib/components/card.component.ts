import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'cp-card', standalone: true, template: '<ng-content />',
  host: { '[class.cp-card--interactive]': 'interactive()', '[class.cp-card--flush]': 'flush()' },
  styles: [`
    :host { display:block; padding:var(--cp-space-5); border:1px solid var(--cp-border); border-radius:var(--cp-radius-lg); background:var(--cp-surface); box-shadow:var(--cp-shadow-sm); }
    :host.cp-card--flush { padding:0; overflow:hidden; }
    :host.cp-card--interactive { transition:transform var(--cp-duration-fast) var(--cp-ease),border-color var(--cp-duration-fast),box-shadow var(--cp-duration-fast); cursor:pointer; }
    :host.cp-card--interactive:hover { transform:translateY(-2px); border-color:color-mix(in srgb,var(--cp-primary) 45%,var(--cp-border)); box-shadow:var(--cp-shadow-lg); }
  `], changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpCardComponent { readonly interactive = input(false); readonly flush = input(false); }
