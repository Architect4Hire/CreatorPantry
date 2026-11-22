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
  viewChildren,
} from '@angular/core';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../../core/confirm.service';
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

/** How many of a suggestion's fields its row names before saying how many more there are. */
const FIELDS_NAMED = 2;

/**
 * The suggestions about one recipe still waiting on the creator (13A.15), one collapsed row each: what kind, when it
 * was asked, how many changes and which fields they touch, and what state it is in — out of date, carrying a safety
 * note. Opening a row shows the recipe page's own proposal panel, which shows exactly what would change and why and
 * takes the creator's yes or no change by change. Read from the server, so a suggestion survives a reload; nothing
 * here accepts, edits or orders a change.
 *
 * <p>A suggestion with a safety caution comes first — a presentation order over the server's newest-first list, so
 * the creator reads it before anything else — and opens by itself, as does the only one in the list. One the recipe
 * has moved past stays collapsed with its two ways out on the row, because there is nothing in it to decide:
 * accepting it would be refused, never quietly rebased. Two or more of those can be declined at once, after one
 * question.</p>
 *
 * <p>The panels stay in the page while a row is closed, so what the creator has ticked, and the watching of a
 * request still being written, survive opening and closing it.</p>
 */
@Component({
  selector: 'cp-recipe-open-suggestions',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, CpSkeletonComponent, CpStatusPillComponent, AiProposalPanelComponent],
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
            <div class="open-head">
              <h3 class="heading" [id]="headingId" tabindex="-1">
                {{ heading() }} <span class="count">({{ shown().length }})</span>
              </h3>
              @if (declinable().length >= 2) {
                <button
                  cpButton
                  type="button"
                  variant="secondary"
                  size="sm"
                  [busy]="decliningAll()"
                  [attr.aria-disabled]="decliningAll() || null"
                  (click)="declineAllOutOfDate()"
                >
                  Decline all out-of-date ({{ declinable().length }})
                </button>
              }
            </div>
            <ul class="list">
              @for (item of shown(); track item.requestId) {
                <li class="item">
                  <details class="cp-disclosure row" [open]="isOpen(item)" (toggle)="onToggle(item, $event)">
                    <summary>
                      <span class="row-title">{{ nameOf(item) }}</span>
                      @if (isOutOfDate(item)) {
                        <cp-status-pill tone="stale">Out of date</cp-status-pill>
                      }
                      @if (item.hasSafetyCaution) {
                        <cp-status-pill tone="warning">Safety note</cp-status-pill>
                      }
                      <span class="row-meta">{{ metaOf(item) }}</span>
                    </summary>
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
                      [condensed]="true"
                      [headingLevel]="4"
                      (decided)="onDecided($event)"
                      (restartRequested)="askAgain.emit(item.taskType)"
                    />
                  </details>

                  <!-- Nothing in an out-of-date suggestion can be applied, so its two ways out are on the row, open
                       or closed, rather than inside it. -->
                  @if (isOutOfDate(item)) {
                    <div class="row-actions">
                      <button cpButton type="button" variant="primary" size="sm" (click)="startAgain(item)">
                        Start again from this recipe
                      </button>
                      @if (panelFor(item)?.mayDecide()) {
                        <button
                          cpButton
                          type="button"
                          variant="secondary"
                          size="sm"
                          [attr.aria-disabled]="panelFor(item)?.busy() || null"
                          (click)="decline(item)"
                        >
                          Decline these
                        </button>
                      }
                      <span class="row-why">Asked against an earlier version, so it can’t be applied.</span>
                    </div>
                  }
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
      gap: var(--cp-space-4);
    }

    .open-head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--cp-space-3);
    }

    .heading {
      margin: 0;
      font-size: var(--cp-font-size-md);
    }

    .count {
      font-weight: 400;
      color: var(--cp-text-muted);
    }

    .list {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .item {
      display: grid;
      gap: var(--cp-space-2);
      padding: var(--cp-space-2) var(--cp-space-4);
      border: 1px solid var(--cp-border);
      border-radius: var(--cp-radius-md);
    }

    /* The row is its summary: what it is, the state it is in, and — under them — what is in it. The pills sit
       beside the title and wrap under it when narrow. */
    .row > summary {
      display: list-item;
    }

    .row-title {
      margin-inline-end: var(--cp-space-2);
    }

    .row-meta {
      display: block;
      font-weight: 400;
      font-size: var(--cp-font-size-sm);
      color: var(--cp-text-muted);
    }

    .row[open] > summary {
      margin-bottom: var(--cp-space-3);
    }

    .row > :not(summary) {
      margin-block-end: var(--cp-space-3);
    }

    .row-actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--cp-space-2) var(--cp-space-3);
      padding-bottom: var(--cp-space-2);
    }

    .row-why {
      font-size: var(--cp-font-size-sm);
      color: var(--cp-text-muted);
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
  private readonly confirmService = inject(ConfirmService);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly panels = viewChildren(AiProposalPanelComponent);

  protected readonly headingId = `cp-open-suggestions-${(openSuggestionsUid += 1)}`;
  protected readonly listed = signal<Listed>({ status: 'loading' });
  private ticket = 0;

  /**
   * What the creator has opened or closed, by request. A row they have not touched follows {@link defaultOpen}, so a
   * list that grows or shrinks never closes a row they are reading.
   */
  private readonly toggled = signal<ReadonlyMap<string, boolean>>(new Map());

  protected readonly decliningAll = signal(false);

  /** What is shown: anything with a safety caution first, otherwise newest first as the server sent it. */
  protected readonly shown = computed(() => {
    const listed = this.listed();
    if (listed.status !== 'ready') return [];
    const others = listed.open.filter((item) => item.requestId !== this.exclude());
    return [...others.filter((item) => item.hasSafetyCaution), ...others.filter((item) => !item.hasSafetyCaution)];
  });

  /** How many are waiting, once read; null while unknown. */
  readonly waiting = computed(() => (this.listed().status === 'ready' ? this.shown().length : null));

  /** The out-of-date suggestions this member may decline, for the one button that declines them together. */
  protected readonly declinable = computed(() =>
    this.shown().flatMap((item) => {
      const panel = this.panels().find((candidate) => candidate.requestId() === item.requestId);
      return panel && (item.isStale || panel.state() === 'stale') && panel.mayDecide() ? [panel] : [];
    }),
  );

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

  /** The panel showing this request, once it exists. Closed rows keep theirs. */
  protected panelFor(item: AiOpenProposal): AiProposalPanelComponent | undefined {
    return this.panels().find((panel) => panel.requestId() === item.requestId);
  }

  /** The list's word for it, or the panel's own once the server has refused to apply it. */
  protected isOutOfDate(item: AiOpenProposal): boolean {
    return item.isStale || this.panelFor(item)?.state() === 'stale';
  }

  /**
   * How many changes, which fields, and when: what a creator decides whether to open on. The fields come from the
   * panel once it has read the suggestion, so a row names them a moment after it first appears.
   */
  protected metaOf(item: AiOpenProposal): string {
    const parts: string[] = [];
    if (item.pendingChangeCount > 0) {
      parts.push(item.pendingChangeCount === 1 ? '1 change' : `${item.pendingChangeCount} changes`);
    } else {
      parts.push('Still being written');
    }

    const labels = [
      ...new Set(
        this.panelFor(item)
          ?.rows()
          .map((row) => row.label) ?? [],
      ),
    ];
    if (labels.length > 0) {
      const named = labels.slice(0, FIELDS_NAMED).join(', ');
      parts.push(labels.length > FIELDS_NAMED ? `${named} +${labels.length - FIELDS_NAMED} more` : named);
    }

    parts.push(`asked ${this.askedOn(item.requestedAt)}`);
    return parts.join(' · ');
  }

  /**
   * Open by itself when there is something to read before answering: a safety note, or the only suggestion there is.
   * Never for one that is out of date — nothing in it can be applied, and its buttons are on the row.
   */
  private defaultOpen(item: AiOpenProposal): boolean {
    if (this.isOutOfDate(item)) return false;
    return item.hasSafetyCaution || this.shown().length === 1;
  }

  protected isOpen(item: AiOpenProposal): boolean {
    return this.toggled().get(item.requestId) ?? this.defaultOpen(item);
  }

  protected onToggle(item: AiOpenProposal, event: Event): void {
    const open = (event.target as HTMLDetailsElement).open;
    if (this.isOpen(item) === open) return;
    this.toggled.update((map) => new Map(map).set(item.requestId, open));
  }

  protected onDecided(result: AiProposalDispositionResult): void {
    this.decided.emit(result);
    void this.reload();
  }

  /** Asks the host to open the way that asks for it again — before the panel has read the suggestion too. */
  protected startAgain(item: AiOpenProposal): void {
    this.askAgain.emit(item.taskType);
  }

  /** One suggestion's own Decline: the panel asks, as it always has. */
  protected decline(item: AiOpenProposal): void {
    const panel = this.panelFor(item);
    if (panel && !panel.busy()) void panel.reject();
  }

  /** Every out-of-date suggestion at once, after one question, in the order they are listed. */
  protected async declineAllOutOfDate(): Promise<void> {
    const targets = this.declinable();
    if (targets.length < 2 || this.decliningAll()) return;

    const confirmed = await this.confirmService.confirm({
      title: `Decline ${targets.length} out-of-date suggestions?`,
      message:
        "They were asked for against an earlier version of your recipe, so none of them can be applied. They can't be reviewed again afterwards. Your recipe stays exactly as it is, and you can always ask for new suggestions.",
      confirmLabel: `Decline ${targets.length} suggestions`,
      cancelLabel: 'Keep them',
      tone: 'danger',
    });
    if (!confirmed) return;

    this.decliningAll.set(true);
    try {
      for (const panel of targets) await panel.declineAsked();
    } finally {
      this.decliningAll.set(false);
    }
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
