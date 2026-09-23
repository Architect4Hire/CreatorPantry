import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
export type CpStatusPillTone = 'neutral'|'progress'|'success'|'warning'|'error'|'stale';
const CP_STATUS_PILL_DEFAULT_ICONS: Record<CpStatusPillTone, string> = { neutral:'○', progress:'◐', success:'✓', warning:'▲', error:'✕', stale:'↻' };
@Component({
  selector:'cp-status-pill', standalone:true,
  template:'@if(!iconHidden()){<span class="icon" aria-hidden="true">{{displayIcon()}}</span>}<span class="label"><ng-content /></span>',
  host:{'[attr.data-tone]':'tone()'},
  styles:[`:host{display:inline-flex;align-items:center;gap:var(--cp-space-1);min-height:1.5rem;padding:0 .55rem;border-radius:var(--cp-radius-full);font-size:var(--cp-font-size-xs);font-weight:700;background:var(--cp-surface-subtle);color:var(--cp-text-muted)}:host([data-tone='progress']){background:var(--cp-blue-soft);color:var(--cp-blue)}:host([data-tone='success']){background:var(--cp-primary-soft);color:var(--cp-primary)}:host([data-tone='warning']){background:var(--cp-orange-soft);color:var(--cp-orange)}:host([data-tone='error']){background:color-mix(in srgb,var(--cp-danger) 14%,var(--cp-surface));color:var(--cp-danger)}:host([data-tone='stale']){background:var(--cp-purple-soft);color:var(--cp-purple)}.icon{font-size:.85em;line-height:1}`],
  changeDetection:ChangeDetectionStrategy.OnPush
}) export class CpStatusPillComponent {
  readonly tone = input<CpStatusPillTone>('neutral');
  readonly icon = input<string>();
  readonly iconHidden = input(false);
  readonly displayIcon = computed(() => this.icon() ?? CP_STATUS_PILL_DEFAULT_ICONS[this.tone()]);
}
