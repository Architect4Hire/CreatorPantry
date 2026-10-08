import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Subject, map, merge, switchMap, tap } from 'rxjs';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { DamAssetUtilization, damCalendarDateText } from '../../models/dam-asset.models';
import { DamAssetService, DamAssetUtilizationOutcome } from '../../services/dam-asset.service';

/** How the first page can stand. Later pages have their own two flags, so a failure there keeps the rows. */
type Phase = 'loading' | 'error' | 'not_found' | 'ready';

/** One page asked for: where it resumes, and which asset's history it belongs to. */
interface PageRequest {
  readonly cursor: string | null;
  readonly generation: number;
}

/**
 * Where and when one asset has been used, newest first (DAM-UI-003).
 *
 * **Its own read, with its own states.** The history gains a row every time the asset goes out, so the server
 * pages it separately from the asset — and a failure here is this section's alone: the picture, the versions
 * and the lineage above it still stand.
 *
 * **Read-only.** Logging a use is the dialog beside this; nothing here writes. The page raises
 * `reloadToken` after one is logged, and this reads again from the top.
 *
 * **Pages are appended by following the server's cursor**, so what is shown is always a true prefix of the
 * history. A cursor is bound to the workspace and the asset, so a change of either starts again without one.
 *
 * **A use is on a calendar date, and is shown as that date.** The weekday beside it is the one the server
 * derived in the workspace's own zone when the use was logged; it is displayed as sent and never recomputed in
 * this browser's zone, where it could come out a day off.
 */
@Component({
  selector: 'cp-dam-asset-usage-history',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent],
  template: `
    @switch (phase()) {
      @case ('loading') {
        <p class="plain" role="status">Loading where this has been used…</p>
      }
      @case ('error') {
        <cp-notice tone="error" role="alert">
          The usage history couldn't be loaded right now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
        </cp-notice>
      }
      @case ('not_found') {
        <cp-notice tone="warning" role="status">The usage history for this picture can't be shown.</cp-notice>
      }
      @default {
        @if (items().length === 0) {
          <p class="plain">Not used yet. Nothing has been logged for this picture.</p>
        } @else {
          <div class="scroll" role="region" tabindex="0" aria-label="Usage history">
            <table>
              <caption class="cp-sr-only">
                Where and when this picture has been used, newest first
              </caption>
              <thead>
                <tr>
                  <th scope="col">Date</th>
                  <th scope="col">Day</th>
                  <th scope="col">Platform</th>
                  <th scope="col">Campaign</th>
                  <th scope="col">Notes</th>
                </tr>
              </thead>
              <tbody>
                @for (use of items(); track use.id) {
                  <tr [attr.data-use-id]="use.id">
                    <th scope="row" tabindex="-1">{{ dateText(use) }}</th>
                    <td>{{ use.utilizedDay }}</td>
                    <td>{{ use.platformKey }}</td>
                    <td>
                      @if (use.campaignName) {
                        {{ use.campaignName }}
                      } @else {
                        <span class="none">None</span>
                      }
                    </td>
                    <td class="notes">
                      @if (use.notes) {
                        {{ use.notes }}
                      } @else {
                        <span class="none">None</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        <div class="pager">
          <p class="summary" role="status" aria-live="polite">{{ summary() }}</p>

          @if (moreFailed()) {
            <cp-notice tone="error" role="alert">Couldn't load more of the history. The rows above are still here.</cp-notice>
          }

          @if (hasMore()) {
            <button cpButton variant="secondary" type="button" [disabled]="loadingMore()" (click)="loadMore()">
              {{ loadingMore() ? 'Loading more…' : moreFailed() ? 'Try loading more again' : 'Load more' }}
            </button>
          }
        </div>
      }
    }
  `,
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
      }
      .plain {
        margin: 0;
      }
      /* Five columns do not fit a narrow screen, so the table scrolls inside its own region rather than
         pushing the page sideways. Focusable, so the scrolling is reachable by keyboard. */
      .scroll {
        overflow-x: auto;
      }
      .scroll:focus-visible,
      th[scope='row']:focus {
        outline: 2px solid var(--cp-focus);
        outline-offset: 2px;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: var(--cp-font-size-sm);
      }
      th,
      td {
        padding: var(--cp-space-2) var(--cp-space-3);
        border-bottom: 1px solid var(--cp-border);
        text-align: left;
        vertical-align: top;
      }
      thead th {
        color: var(--cp-text-muted);
        font-weight: 600;
        white-space: nowrap;
      }
      th[scope='row'] {
        font-weight: 600;
        white-space: nowrap;
      }
      .notes {
        min-width: 12rem;
        white-space: pre-wrap;
        overflow-wrap: anywhere;
      }
      .none {
        color: var(--cp-text-muted);
      }
      .pager {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--cp-space-3);
      }
      .summary {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-xs);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamAssetUsageHistoryComponent {
  private readonly assets = inject(DamAssetService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();
  readonly assetId = input.required<string>();
  /**
   * Raise this to read the history again from the top — after a use is logged, say. Its value means nothing;
   * only that it changed.
   */
  readonly reloadToken = input(0);

  protected readonly phase = signal<Phase>('loading');
  protected readonly items = signal<readonly DamAssetUtilization[]>([]);
  private readonly nextCursor = signal<string | null>(null);
  /** Carried across pages: asked for once, because following a cursor cannot change it. */
  private readonly total = signal<number | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly moreFailed = signal(false);

  /**
   * Which asset's history the rows on screen belong to. Raised when the asset or workspace changes, so an
   * answer still in flight for the previous one is dropped rather than shown under this one.
   */
  private generation = 0;

  private readonly requests$ = new Subject<PageRequest>();
  private readonly target = computed(() => ({ slug: this.workspaceSlug(), id: this.assetId(), reload: this.reloadToken() }));

  protected readonly hasMore = computed(() => this.phase() === 'ready' && this.nextCursor() !== null);

  protected readonly summary = computed(() => {
    if (this.phase() !== 'ready') return '';

    const shown = this.items().length;
    if (shown === 0) return '';

    const total = this.total();
    const counted = total === null ? `${shown} ${shown === 1 ? 'use' : 'uses'}` : `${shown} of ${total}`;

    return `Showing ${counted}, newest first.`;
  });

  constructor() {
    merge(
      // A new asset or workspace, or a request to read again: everything read before goes first.
      toObservable(this.target).pipe(
        map((): PageRequest => {
          this.generation += 1;
          this.items.set([]);
          this.nextCursor.set(null);
          this.total.set(null);
          this.loadingMore.set(false);
          this.moreFailed.set(false);

          return this.firstPage();
        }),
      ),
      this.requests$,
    )
      .pipe(
        tap((request) => {
          if (request.cursor === null) this.phase.set('loading');
        }),
        // switchMap, so a page still in flight for one asset cannot land after the next asset's request.
        switchMap((request) => {
          const { slug, id } = this.target();

          return this.assets.utilization(slug, id, request.cursor).pipe(map((outcome) => ({ request, outcome })));
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ request, outcome }) => this.apply(request, outcome));
  }

  protected dateText(use: DamAssetUtilization): string {
    return damCalendarDateText(use.utilizedOn);
  }

  protected retry(): void {
    this.requests$.next(this.firstPage());
  }

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (this.phase() !== 'ready' || cursor === null || this.loadingMore()) return;

    this.loadingMore.set(true);
    this.moreFailed.set(false);
    this.requests$.next({ cursor, generation: this.generation });
  }

  private firstPage(): PageRequest {
    return { cursor: null, generation: this.generation };
  }

  private apply(request: PageRequest, outcome: DamAssetUtilizationOutcome): void {
    if (request.generation !== this.generation) return;

    const first = request.cursor === null;

    switch (outcome.status) {
      case 'found': {
        this.nextCursor.set(outcome.page.nextCursor);

        if (first) {
          this.total.set(outcome.page.totalCount);
          this.items.set(outcome.page.items);
          this.phase.set('ready');
          return;
        }

        const held = new Set(this.items().map((item) => item.id));
        const added = outcome.page.items.filter((item) => !held.has(item.id));

        this.items.update((items) => [...items, ...added]);
        this.loadingMore.set(false);
        if (added.length > 0) this.focusRow(added[0].id);
        return;
      }

      case 'cursor_expired':
        // The position is gone, but the history is not: read it again from the start.
        this.nextCursor.set(null);
        this.loadingMore.set(false);
        this.requests$.next(this.firstPage());
        return;

      case 'not_found':
        // The asset has gone since the page was read — removed, most likely. Nothing to retry.
        this.items.set([]);
        this.loadingMore.set(false);
        this.phase.set('not_found');
        return;

      default:
        if (first) {
          this.phase.set('error');
        } else {
          // The rows already read are still true, so they stay; only the next page is missing.
          this.loadingMore.set(false);
          this.moreFailed.set(true);
        }
    }
  }

  /**
   * Put focus on the first row a "Load more" added: the button pressed may have just gone, and focus left on a
   * removed element falls back to the top of the document.
   */
  private focusRow(useId: string): void {
    afterNextRender(
      () => {
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-use-id="${CSS.escape(useId)}"] th`)
          ?.focus();
      },
      { injector: this.injector },
    );
  }
}
