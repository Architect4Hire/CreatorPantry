import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { CONTENT_BOARD_COLUMN_LABELS, ContentCardDetail } from '../../../models/content-board.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { ContentBoardService } from '../../../services/content-board.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { runCardId } from './publish-run';

type Read =
  | { readonly status: 'loading' | 'unavailable' | 'no-card' | 'gone' }
  | { readonly status: 'ready'; readonly detail: ContentCardDetail };

/**
 * "Check it's ready", the optional second step of "Plan and publish content" (13A.22).
 *
 * <p><strong>A last read, and nothing else.</strong> It shows the card's own words and what it points at, so
 * the creator can see what is about to go out; it writes nothing, and the card page is where an edit is made.
 * The step is optional, so Skip is the shell's and costs nothing.</p>
 *
 * <p><strong>It does not say the card is fit to publish.</strong> Only the creator can say that, which is why
 * this step is an acknowledgement rather than a check with a verdict.</p>
 */
@Component({
  selector: 'cp-publish-check-step',
  standalone: true,
  imports: [RouterLink, CpBadgeComponent, CpButtonComponent, CpFactComponent, CpFactsComponent, CpNoticeComponent, CpSkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{ read().status === 'loading' ? 'Reading your card…' : '' }}</p>
    @switch (read().status) {
      @case ('loading') {
        <!-- The card arriving, as a few lines. -->
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('no-card') {
        <cp-notice role="note">Go back a step and choose the card you want to publish.</cp-notice>
      }
      @case ('gone') {
        <cp-notice tone="warning" role="status">
          That card can’t be found any more. Go back a step and choose another.
        </cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your card couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (detail(); as shown) {
          <p class="plain"><strong>{{ shown.card.title }}</strong></p>
          <dl cpFacts>
            <div cpFact label="On the board">{{ column(shown) }}</div>
            <div cpFact label="Planned for">{{ shown.card.plannedDate ?? 'No day yet' }}</div>
            <div cpFact label="Channel">{{ shown.card.channelKey ?? 'Not set' }}</div>
            <div cpFact label="Picture">{{ shown.card.lineage.mediaAssetId ? 'One is linked' : 'None linked' }}</div>
          </dl>
          @if (shown.description; as words) {
            <p class="prose">{{ words }}</p>
          } @else {
            <cp-notice role="note">
              This card has no words on it yet. You can still pick a time, and write them on the card before it goes out.
            </cp-notice>
          }
          @if (shown.card.lineage.mediaAssetId) {
            <cp-notice role="note">
              A picture can’t be sent through the provider yet, so what goes out is the words. The picture stays on
              the card.
            </cp-notice>
          }
          <p class="plain">
            <a cpButton variant="secondary" size="sm" [routerLink]="['/', workspaceSlug(), 'content-board', shown.card.id]">
              Open the card
            </a>
            <cp-badge>Nothing is scheduled yet</cp-badge>
          </p>
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .plain {
      margin: 0;
      display: flex;
      flex-wrap: wrap;
      gap: var(--cp-space-3);
      align-items: center;
    }

    .prose {
      margin: 0;
      max-width: var(--cp-measure-prose);
      white-space: pre-wrap;
    }
  `,
})
export class PublishCheckStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly board = inject(ContentBoardService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly read = signal<Read>({ status: 'loading' });

  private gone = false;

  protected readonly detail = computed(() => {
    const read = this.read();

    return read.status === 'ready' ? read.detail : null;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    // An acknowledgement: there is nothing to keep, and reading the card is what the step is for.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      ready: computed(() => this.detail() !== null),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
  }

  protected column(detail: ContentCardDetail): string {
    return CONTENT_BOARD_COLUMN_LABELS[detail.card.column];
  }

  async load(): Promise<void> {
    const slug = this.workspaceSlug();
    const cardId = runCardId(this.run());
    if (cardId === null) {
      this.read.set({ status: 'no-card' });

      return;
    }

    this.read.set({ status: 'loading' });
    const outcome = await firstValueFrom(this.board.detail(slug, cardId));
    if (this.gone || slug !== this.workspaceSlug()) return;

    this.read.set(
      outcome.status === 'found'
        ? { status: 'ready', detail: outcome.detail }
        : { status: outcome.status === 'not_found' ? 'gone' : 'unavailable' },
    );
  }
}
