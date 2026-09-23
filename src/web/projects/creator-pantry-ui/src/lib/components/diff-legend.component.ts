import { ChangeDetectionStrategy, Component, input } from '@angular/core';
export type CpDiffKind = 'added'|'removed'|'changed'|'moved'|'unchanged'|'warning'|'selected';
const CP_DIFF_LEGEND_GLYPHS: Record<CpDiffKind, string> = { added:'+', removed:'−', changed:'~', moved:'↕', unchanged:'·', warning:'!', selected:'✓' };
const CP_DIFF_LEGEND_DEFAULT_LABELS: Record<CpDiffKind, string> = { added:'Added', removed:'Removed', changed:'Changed', moved:'Moved', unchanged:'Unchanged', warning:'Needs attention', selected:'Selected' };
const CP_DIFF_LEGEND_ORDER: CpDiffKind[] = ['added','removed','changed','moved','unchanged','warning','selected'];
@Component({
  selector:'cp-diff-legend', standalone:true,
  template:'<ul class="legend" role="list">@for(kind of show(); track kind){<li class="entry" [attr.data-kind]="kind"><span class="swatch" aria-hidden="true">{{glyphFor(kind)}}</span><span class="label">{{labelFor(kind)}}</span></li>}</ul>',
  styles:[`:host{display:block}.legend{display:flex;flex-wrap:wrap;gap:var(--cp-space-3);margin:0;padding:0;list-style:none}.entry{display:inline-flex;align-items:center;gap:var(--cp-space-1);font-size:var(--cp-font-size-sm);color:var(--cp-text)}.swatch{display:inline-flex;align-items:center;justify-content:center;min-width:1.25rem;height:1.25rem;padding:0 .25rem;border-radius:var(--cp-radius-sm);font-size:var(--cp-font-size-xs);font-weight:700;line-height:1;background:var(--cp-surface-subtle);color:var(--cp-text-muted)}.entry[data-kind='added'] .swatch{background:var(--cp-primary-soft);color:var(--cp-primary)}.entry[data-kind='removed'] .swatch{background:color-mix(in srgb,var(--cp-danger) 14%,var(--cp-surface));color:var(--cp-danger)}.entry[data-kind='changed'] .swatch{background:var(--cp-blue-soft);color:var(--cp-blue)}.entry[data-kind='moved'] .swatch{background:var(--cp-purple-soft);color:var(--cp-purple)}.entry[data-kind='unchanged'] .swatch{background:var(--cp-surface-subtle);color:var(--cp-text-muted)}.entry[data-kind='warning'] .swatch{background:var(--cp-orange-soft);color:var(--cp-orange)}.entry[data-kind='selected'] .swatch{background:var(--cp-primary-soft);color:var(--cp-primary)}`],
  changeDetection:ChangeDetectionStrategy.OnPush
}) export class CpDiffLegendComponent {
  readonly show = input<CpDiffKind[]>(CP_DIFF_LEGEND_ORDER);
  readonly labels = input<Partial<Record<CpDiffKind, string>>>({});
  glyphFor(kind: CpDiffKind): string { return CP_DIFF_LEGEND_GLYPHS[kind]; }
  labelFor(kind: CpDiffKind): string { return this.labels()[kind] ?? CP_DIFF_LEGEND_DEFAULT_LABELS[kind]; }
}
