import { ChangeDetectionStrategy, Component, ElementRef, computed, input, viewChild } from '@angular/core';

export type CpListShellState = 'loading' | 'error' | 'empty' | 'ready';

let cpListShellUid = 0;

@Component({
  selector: 'cp-list-shell', standalone: true,
  template: `<section [attr.aria-labelledby]="headingId"><header class="head"><h2 [id]="headingId">{{heading()}}</h2><div class="actions"><ng-content select="[cpListShellActions]" /></div></header><div class="body" [attr.aria-busy]="state()==='loading' ? true : null">@switch(state()){@case('loading'){<div class="status" role="status" aria-live="polite">Loading…</div>}@case('error'){<div class="alert" role="alert">{{errorMessage()}}</div>}@case('empty'){<div class="empty-slot" #emptyWrap><ng-content select="[cpListShellEmpty]" /></div>@if(!hasCustomEmpty()){<p class="empty-fallback">{{emptyMessage()}}</p>}}@case('ready'){<div class="scroll" role="region" tabindex="0" [attr.aria-labelledby]="headingId"><ng-content /></div>}}</div><footer class="pagination"><ng-content select="[cpListShellPagination]" /></footer></section>`,
  styles: [`:host{display:block}/* Consumers: mark a selected row in projected content with background:var(--cp-primary-soft); this shell does not own row selection state. */.head{display:flex;align-items:center;justify-content:space-between;gap:var(--cp-space-4);flex-wrap:wrap}h2{margin:0;font:400 var(--cp-font-size-xl)/var(--cp-leading-tight) var(--cp-font-display);color:var(--cp-text)}.actions{display:flex;align-items:center;gap:var(--cp-space-2)}.body{margin-top:var(--cp-space-4);min-height:var(--cp-space-10)}.status,.empty-fallback{margin:0;color:var(--cp-text-muted);font-size:var(--cp-font-size-sm)}.alert{margin:0;color:var(--cp-danger);font-size:var(--cp-font-size-sm)}.scroll{overflow-x:auto}.pagination{display:flex;align-items:center;justify-content:flex-end;gap:var(--cp-space-2);margin-top:var(--cp-space-4)}.pagination:empty{display:none}`],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpListShellComponent {
  readonly heading = input.required<string>();
  readonly state = input<CpListShellState>('ready');
  readonly errorMessage = input('');
  readonly emptyMessage = input('No results.');
  readonly headingId = `cp-list-shell-heading-${cpListShellUid++}`;
  private readonly emptySlot = viewChild<ElementRef<HTMLElement>>('emptyWrap');
  readonly hasCustomEmpty = computed(() => (this.emptySlot()?.nativeElement.childElementCount ?? 0) > 0);
}
