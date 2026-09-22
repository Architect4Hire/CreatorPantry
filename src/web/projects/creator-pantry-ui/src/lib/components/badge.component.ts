import { ChangeDetectionStrategy, Component, input } from '@angular/core';
export type CpTone = 'neutral'|'success'|'pink'|'orange'|'purple'|'blue';
@Component({
  selector:'cp-badge', standalone:true, template:'<ng-content />',
  host:{'[attr.data-tone]':'tone()'},
  styles:[`:host{display:inline-flex;align-items:center;min-height:1.5rem;padding:0 .55rem;border-radius:var(--cp-radius-full);font-size:var(--cp-font-size-xs);font-weight:700;background:var(--cp-surface-subtle);color:var(--cp-text-muted)}:host([data-tone='success']){background:var(--cp-primary-soft);color:var(--cp-primary)}:host([data-tone='pink']){background:var(--cp-pink-soft);color:var(--cp-pink)}:host([data-tone='orange']){background:var(--cp-orange-soft);color:var(--cp-orange)}:host([data-tone='purple']){background:var(--cp-purple-soft);color:var(--cp-purple)}:host([data-tone='blue']){background:var(--cp-blue-soft);color:var(--cp-blue)}`],
  changeDetection:ChangeDetectionStrategy.OnPush
}) export class CpBadgeComponent { readonly tone=input<CpTone>('neutral'); }
