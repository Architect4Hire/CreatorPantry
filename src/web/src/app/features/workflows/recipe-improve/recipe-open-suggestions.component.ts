import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

import { AiOpenProposal, AiProposalDispositionResult, AiTaskType } from '../../../models/ai-proposal.models';
import { AiProposalService } from '../../../services/ai-proposal.service';
import { AiProposalPanelComponent } from '../../ai/ai-proposal-panel.component';

type Listed =
  | { readonly status: 'loading' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly open: readonly AiOpenProposal[] };

let openSuggestionsUid = 0;

/** What each kind of suggestion is called, in the creator's words. */
const KIND_NAMES: Partial<Record<AiTaskType, string>> = {
  RecipeRevision: 'Suggested changes',
  RecipeAdaptation: 'An adapted version',
};

/**
 * The suggestions about one recipe still waiting on the creator (13A.15): each in the recipe page's own proposal
 * panel, which shows exactly what would change and why, and takes the creator's yes or no change by change. Read from
 * the server, so a suggestion survives a reload; nothing here accepts, edits or orders a change.
 *
 * <p>A suggestion with a safety caution comes first — a presentation order over the server's newest-first list, so
 * the creator reads it before anything else. One the recipe has moved past opens in the panel's stale state: its
 * changes to read, a fresh ask, and a way to decline it, but nothing to keep — accepting it would be refused, never
 * quietly rebased.</p>
 */
@Component({
  selector: 'cp-recipe-open-suggestions',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, CpSkeletonComponent, AiProposalPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, outside the busy lines, so the step says once what it is waiting for (AU.7). -->
    <p class="cp-sr-only" role="status">{{
      listed().status === 'loading' ? 'Looking for suggestions waiting for you…' : ''
    }}</p>
    @switch (listed().status) {
      @case ('loading') {
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          The suggestions waiting for you couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="reload()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (shown().length === 0) {
          @if (sayWhenEmpty()) {
            <p class="plain">Nothing is waiting for your answer.</p>
          }
        } @else {
          <section class="open" [attr.aria-labelledby]="headingId">
            <h3 class="heading" [id]="headingId" tabindex="-1">{{ heading() }}</h3>
            <ul class="list">
              @for (item of shown(); track item.requestId) {
                <li class="item">
                  <p class="plain">
                    <strong>{{ nameOf(item) }}</strong
                    >, asked for {{ askedOn(item.requestedAt) }}.
                    @if (item.pendingChangeCount > 0) {
                      {{
                        item.pendingChangeCount === 1 ? 'One change waits' : item.pendingChangeCount + ' changes wait'
                      }}
                      for your answer.
                    } @else {
                      Still being written.
                    }
                  </p>
                  @if (item.hasSafetyCaution) {
                    <cp-notice tone="warning" role="note"
                      >This one carries a safety note. Read it before you decide.</cp-notice
                    >
                  }
                  <cp-ai-proposal-panel
                    [workspaceSlug]="workspaceSlug()"
                    [recipeId]="recipeId()"
                    [requestId]="item.requestId"
                    [knownStale]="item.isStale"
                    [headingLevel]="4"
                    (decided)="onDecided($event)"
                    (restartRequested)="askAgain.emit(item.taskType)"
                  />
                </li>
              }
            </ul>
          </section>
        }
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }

    .open,
    .list {
      display: grid;
      gap: var(--cp-space-5);
    }

    .heading {
      margin: 0;
      font-size: var(--cp-font-size-md);
    }

    .list {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .item {
      display: grid;
      gap: var(--cp-space-3);
    }

    .plain {
      margin: 0;
    }
  `,
})
export class RecipeOpenSuggestionsComponent {
  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();
  readonly heading = input('Waiting for your answer');
  /** A request shown elsewhere on the page — by the tool that asked for it — so it is not shown twice. */
  readonly exclude = input<string | null>(null);
  /** Say "nothing waiting" rather than show nothing at all. */
  readonly sayWhenEmpty = input(false);

  /** A suggestion was decided: the recipe may have a new version. */
  readonly decided = output<AiProposalDispositionResult>();
  /** The creator wants a stale suggestion asked for again, against the recipe as it is now. */
  readonly askAgain = output<AiTaskType>();

  private readonly proposals = inject(AiProposalService);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);

  protected readonly headingId = `cp-open-suggestions-${(openSuggestionsUid += 1)}`;
  protected readonly listed = signal<Listed>({ status: 'loading' });
  private ticket = 0;

  /** What is shown: anything with a safety caution first, otherwise newest first as the server sent it. */
  protected readonly shown = computed(() => {
    const listed = this.listed();
    if (listed.status !== 'ready') return [];
    const others = listed.open.filter((item) => item.requestId !== this.exclude());
    return [...others.filter((item) => item.hasSafetyCaution), ...others.filter((item) => !item.hasSafetyCaution)];
  });

  /** How many are waiting, once read; null while unknown. */
  readonly waiting = computed(() => (this.listed().status === 'ready' ? this.shown().length : null));

  constructor() {
    effect(() => {
      const slug = this.workspaceSlug();
      const id = this.recipeId();
      untracked(() => void this.load(slug, id));
    });
  }

  /** Reads the list again: after a decision, a save, or the creator's Try again. */
  reload(): Promise<void> {
    return this.load(this.workspaceSlug(), this.recipeId());
  }

  protected nameOf(item: AiOpenProposal): string {
    return KIND_NAMES[item.taskType] ?? 'A suggestion';
  }

  protected askedOn(iso: string): string {
    return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  }

  protected onDecided(result: AiProposalDispositionResult): void {
    this.decided.emit(result);
    void this.reload();
  }

  /** Puts focus on the list's heading — after a decision took away the panel that held it. False when the list is gone. */
  focusHeading(): boolean {
    const heading = this.host.nativeElement.querySelector<HTMLElement>(`#${this.headingId}`);
    heading?.focus();
    return heading !== null;
  }

  private async load(slug: string, recipeId: string): Promise<void> {
    const ticket = ++this.ticket;
    if (this.listed().status !== 'ready') this.listed.set({ status: 'loading' });
    const outcome = await this.proposals.listOpen(slug, recipeId);
    if (ticket !== this.ticket) return;
    this.listed.set(outcome.status === 'found' ? { status: 'ready', open: outcome.open } : { status: 'unavailable' });
  }
}
