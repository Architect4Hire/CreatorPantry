import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  input,
  signal,
  viewChild,
} from '@angular/core';

import { CpEnterDirective } from './enter.directive';
import { CpSkeletonComponent } from './skeleton.component';

export type CpListShellState = 'loading' | 'error' | 'empty' | 'ready';
/** The shape a loading list draws: a grid of media cards, or rows of text (AU.2.3). */
export type CpListShellLoadingLayout = 'cards' | 'rows';

let cpListShellUid = 0;

/*
 * While loading, the shell draws skeletons in the shape of what is coming, and its one polite status region — always
 * in the page, outside the aria-busy body, visually hidden — reads "Loading…". A region that already exists and
 * changes its text is what a screen reader announces; one inserted with its text, or held inside a busy subtree,
 * may not be. Nobody sees it. A list whose shape is neither rows nor cards — a board's named columns — projects its
 * own `[cpListShellLoading]`, drawn in place of the default skeleton under the same status and `aria-busy`, and hidden
 * from a screen reader by the shell, as a skeleton is (AU.6.1).
 *
 * The ready content scrolls sideways in its own region, which is positioned so that something visually hidden in
 * it — a table header's `.cp-sr-only` — is clipped with it rather than widening the page (AU.0's picture page).
 *
 * While the list is empty, the pagination footer is said to a screen reader but not shown, because a feature's
 * result summary there ("No recipes yet.") would only repeat the empty state above it (AU.0), and the summary is how
 * a screen reader hears that a filter left nothing. Only while empty: a pager that is loading the next page stays
 * where the creator's focus is. Clipped, not inert, so an empty list's footer holds status text and no control.
 */
@Component({
  selector: 'cp-list-shell',
  standalone: true,
  imports: [CpEnterDirective, CpSkeletonComponent],
  template: `<section [attr.aria-labelledby]="headingId"
    ><header class="head"
      ><h2 [id]="headingId">{{ heading() }}</h2
      ><div class="actions"><ng-content select="[cpListShellActions]" /></div></header
    ><p class="offscreen" role="status" aria-live="polite">{{ state() === 'loading' ? 'Loading…' : '' }}</p
    ><div class="body" [attr.aria-busy]="state() === 'loading' ? true : null">
      @switch (state()) {
        @case ('loading') {
          <div class="loading-slot" aria-hidden="true" #loadingWrap><ng-content select="[cpListShellLoading]" /></div>
          @if (!hasCustomLoading()) {
            <cp-skeleton
              [shape]="loadingLayout() === 'cards' ? 'card' : 'line'"
              [count]="loadingLayout() === 'cards' ? 6 : 5"
            />
          }
        }
        @case ('error') {
          <div class="alert" role="alert">{{ errorMessage() }}</div>
        }
        @case ('empty') {
          <div class="empty-slot" #emptyWrap><ng-content select="[cpListShellEmpty]" /></div>
          @if (!hasCustomEmpty()) {
            <p class="empty-fallback">{{ emptyMessage() }}</p>
          }
        }
        @case ('ready') {
          <div class="scroll" [cpEnter]="!hasBeenReady()" role="region" tabindex="0" [attr.aria-labelledby]="headingId"
            ><ng-content
          /></div>
        }
      }</div
    ><footer class="pagination" [class.offscreen]="state() === 'empty'"
      ><ng-content select="[cpListShellPagination]" /></footer
  ></section>`,
  styles: [
    `
      :host {
        display: block;
      } /* Consumers: mark a selected row in projected content with background:var(--cp-primary-soft); this shell does not own row selection state. */
      .head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--cp-space-4);
        flex-wrap: wrap;
      }
      h2 {
        margin: 0;
        font: 400 var(--cp-font-size-xl)/var(--cp-leading-tight) var(--cp-font-display);
        color: var(--cp-text);
      }
      .actions {
        display: flex;
        align-items: center;
        gap: var(--cp-space-2);
      }
      .body {
        margin-top: var(--cp-space-4);
        min-height: var(--cp-space-10);
      }
      .empty-fallback {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
      .alert {
        margin: 0;
        color: var(--cp-danger);
        font-size: var(--cp-font-size-sm);
      }
      .scroll {
        position: relative;
        overflow-x: auto;
      }
      .loading-slot:empty {
        display: none;
      }
      .pagination {
        display: flex;
        align-items: center;
        justify-content: flex-end;
        gap: var(--cp-space-2);
        margin-top: var(--cp-space-4);
      }
      .pagination:empty {
        display: none;
      }
      .offscreen {
        position: absolute;
        width: 1px;
        height: 1px;
        margin: -1px;
        padding: 0;
        overflow: hidden;
        clip: rect(0, 0, 0, 0);
        white-space: nowrap;
        border: 0;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpListShellComponent {
  readonly heading = input.required<string>();
  readonly state = input<CpListShellState>('ready');
  readonly loadingLayout = input<CpListShellLoadingLayout>('rows');
  readonly errorMessage = input('');
  readonly emptyMessage = input('No results.');
  readonly headingId = `cp-list-shell-heading-${cpListShellUid++}`;
  /* The ready content arrives once (AU.3.4): the first time the list is ready and not again, because a filter
     or a save that reloads the same list builds the same element afresh, and a second entrance would read as
     the page starting over rather than as the list landing. */
  protected readonly hasBeenReady = signal(false);
  private readonly emptySlot = viewChild<ElementRef<HTMLElement>>('emptyWrap');
  private readonly loadingSlot = viewChild<ElementRef<HTMLElement>>('loadingWrap');

  constructor() {
    // After the render, not during it: a component effect runs before its own view is refreshed, so latching
    // there would mean the first ready render already read `true` and nothing ever arrived.
    afterRenderEffect(() => {
      if (this.state() === 'ready') this.hasBeenReady.set(true);
    });
  }

  readonly hasCustomEmpty = computed(() => (this.emptySlot()?.nativeElement.childElementCount ?? 0) > 0);
  /** A feature that knows its own shape better than rows or cards — a board's columns — projects it (AU.6.1). */
  readonly hasCustomLoading = computed(() => (this.loadingSlot()?.nativeElement.childElementCount ?? 0) > 0);
}
