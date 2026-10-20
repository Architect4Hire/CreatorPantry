import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpChoiceGroupComponent, CpChoiceOption, CpNoticeComponent } from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { CONTENT_BOARD_COLUMN_LABELS, ContentCard, NO_BOARD_FILTERS } from '../../../models/content-board.models';
import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';
import { ContentBoardService } from '../../../services/content-board.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { cardPointer, runCardId } from './publish-run';

type Read =
  | { readonly status: 'loading' | 'unavailable' }
  | { readonly status: 'ready'; readonly cards: readonly ContentCard[]; readonly more: boolean };

/** The most cards offered at once. More than this and the board itself is the better place to choose. */
const MOST = 25;

/**
 * "Choose what to publish", the first step of "Plan and publish content" (13A.22).
 *
 * <p><strong>A choice, not a search.</strong> The cards already on the board are offered as a list of answers —
 * one chosen at a time, by keyboard as well as by pointer — and nothing is created here. A run started from a
 * card arrives with that card already chosen.</p>
 *
 * <p><strong>Nothing is scheduled by choosing.</strong> The card is pointed at the step, so leaving and coming
 * back keeps it; the time and the booking are the step after next.</p>
 */
@Component({
  selector: 'cp-publish-choose-step',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpChoiceGroupComponent, CpNoticeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (read().status) {
      @case ('loading') {
        <p class="cp-muted" role="status">Reading your board…</p>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your board couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (options().length === 0) {
          <cp-notice role="note">
            There is nothing on your board yet. Put the work on a card first — that is what gets a time and goes out.
            <a [routerLink]="['/', workspaceSlug(), 'content-board']">Open your board</a>
          </cp-notice>
        } @else {
          <cp-choice-group
            label="Which card goes out"
            idPrefix="publish-choose"
            [options]="options()"
            [value]="picked()"
            (valueChange)="pick($event)"
          />
          @if (keepFailed()) {
            <cp-notice tone="warning" role="status">
              This workflow couldn’t be pointed at that card just now. Continue will still name it.
            </cp-notice>
          }
          @if (read(); as shown) {
            @if (shown.status === 'ready' && shown.more) {
              <p class="cp-muted">
                Showing the first {{ most }} cards.
                <a [routerLink]="['/', workspaceSlug(), 'content-board']">Open your board</a>
                to work from one further down it.
              </p>
            }
          }
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    p {
      margin: 0;
    }
  `,
})
export class PublishChooseStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  protected readonly most = MOST;

  private readonly board = inject(ContentBoardService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly read = signal<Read>({ status: 'loading' });
  protected readonly keepFailed = signal(false);

  /** The card chosen here, or the one the run already holds. */
  private readonly chosen = signal<string | null>(null);

  private gone = false;

  protected readonly picked = computed<readonly string[]>(() => {
    const chosen = this.chosen() ?? runCardId(this.run());

    return chosen === null ? [] : [chosen];
  });

  protected readonly options = computed<readonly CpChoiceOption[]>(() =>
    this.cards().map((card) => ({
      value: card.id,
      label: card.title,
      hint: this.where(card),
      example: card.descriptionPreview ?? undefined,
    })),
  );

  private readonly cards = computed<readonly ContentCard[]>(() => {
    const read = this.read();

    return read.status === 'ready' ? read.cards : [];
  });

  private readonly pointer = computed<WorkflowResourcePointer | null>(() => {
    const chosen = this.picked()[0];

    return chosen === undefined ? null : cardPointer(chosen);
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    // Choosing writes nothing of its own: the pointer on the run is all this step keeps.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      draft: this.pointer,
      results: computed(() => {
        const pointer = this.pointer();

        return pointer === null ? [] : [pointer];
      }),
      ready: computed(() => this.pointer() !== null),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    const slug = this.workspaceSlug();
    this.read.set({ status: 'loading' });

    const outcome = await firstValueFrom(this.board.list(slug, NO_BOARD_FILTERS, null, null, MOST));
    if (this.gone || slug !== this.workspaceSlug()) return;

    this.read.set(
      outcome.status === 'found'
        ? { status: 'ready', cards: outcome.page.items, more: outcome.page.nextCursor !== null }
        : { status: 'unavailable' },
    );
  }

  /** Where the card stands and when it is planned for, in a line under its name. */
  protected where(card: ContentCard): string {
    const column = CONTENT_BOARD_COLUMN_LABELS[card.column];

    return card.plannedDate === null ? column : `${column} · planned for ${card.plannedDate}`;
  }

  protected pick(value: readonly string[]): void {
    const cardId = value[value.length - 1] ?? null;
    if (cardId === null || cardId === this.chosen()) return;

    this.chosen.set(cardId);
    this.keepFailed.set(false);

    const card = this.cards().find((candidate) => candidate.id === cardId);
    this.port.announce(card === undefined ? 'Card chosen.' : `Chosen: “${card.title}”. Nothing is scheduled yet.`);

    void this.port.keepDraft(cardPointer(cardId)).then((kept) => {
      if (!this.gone) this.keepFailed.set(!kept);
    });
  }
}
