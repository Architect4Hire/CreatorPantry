import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpEmptyStateComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { CONTENT_BOARD_COLUMN_LABELS, ContentCard } from '../../../models/content-board.models';
import { CreativeContext, linkedRecipeOf, pictureReferenceOf } from '../../../models/creative-context.models';
import { ChannelPostPackage } from '../../../models/channel-post.models';
import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';
import { ChannelPostService } from '../../../services/channel-post.service';
import { ContentBoardService } from '../../../services/content-board.service';
import { CreativeContextService } from '../../../services/creative-context.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { newestLinked, runContextId } from '../creative-work/creative-work-run';

type Read =
  | { readonly status: 'loading' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'no-work' }
  | { readonly status: 'ready'; readonly context: CreativeContext; readonly posts: ChannelPostPackage | null };

/** What a press can end in, said in a sentence. */
const PROBLEMS: Readonly<Record<string, string>> = {
  invalid: 'The card couldn’t be made from this work. Check it has a name or a recipe, then try again.',
  refused: 'Something this work names can’t go on a card any more — it may have been removed. Read it again and try once more.',
  forbidden: 'You don’t have permission to add cards to the board.',
  not_found: 'This piece of work can’t be found any more.',
  stale: 'The board changed while this was being added. Trying again is safe.',
  unavailable: 'The card couldn’t be added just now. Trying again is safe — it won’t make two.',
};

const KEY_PREFIX = 'cp.workflow-board-key.';

/**
 * "Add it to your content board", the last step of "Create social posts" (13A.20, RCPUB-006).
 *
 * <p><strong>One card, made from the work.</strong> It links the piece of work, its recipe, its posts and the
 * picture chosen for them — each a record the server checks this workspace owns — and goes in To do. It plans the
 * work: nothing is scheduled and nothing is posted.</p>
 *
 * <p><strong>Never two.</strong> The card's request key is kept for this run in the browser's session, so a lost
 * answer, a second press or a reload sends the same request; and once there is a card the run holds it, so the step
 * shows that card rather than offering another.</p>
 */
@Component({
  selector: 'cp-social-board-step',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpEmptyStateComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always in the page, so it is announced when its words change; the skeleton under it is aria-hidden. -->
    <p class="cp-sr-only" role="status">{{ card() === null && read().status === 'loading' ? 'Reading your posts…' : '' }}</p>

    @if (card(); as onBoard) {
      <cp-notice tone="success" role="status">
        On your board as “{{ onBoard.title }}”, in {{ columnLabel(onBoard) }}. Nothing is scheduled or posted yet.
        <a [routerLink]="['/', workspaceSlug(), 'content-board', onBoard.id]">Open the card</a>
      </cp-notice>
      @if (keepFailed()) {
        <cp-notice tone="warning" role="status">
          The card is on your board, but this workflow couldn’t be pointed at it just now. Continue will still name it.
        </cp-notice>
      }
    } @else {
      @switch (read().status) {
        @case ('loading') {
          <!-- The shape of what is coming: a sentence and the card's three facts (APP-003). -->
          <div class="loading" aria-busy="true">
            <cp-skeleton shape="line" />
            <cp-skeleton shape="line" [count]="3" />
          </div>
        }
        @case ('no-work') {
          <cp-empty-state
            title="Nothing for the board yet"
            description="There is nothing to put on the board yet. Go back to the first step to say what the posts are about."
            illustration="board"
          />
        }
        @case ('unavailable') {
          <cp-notice tone="error" role="alert">
            Your posts couldn’t be read just now.
            <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
          </cp-notice>
        }
        @case ('ready') {
          <p class="plain">One card goes in To do, so you can plan when it goes out. Nothing is scheduled or posted.</p>
          <dl cpFacts>
            <div cpFact label="Called">{{ cardTitle() }}</div>
            <div cpFact label="Posts you kept">{{ keptText() }}</div>
            <div cpFact label="Picture">{{ pictureText() }}</div>
          </dl>
          @if (keptCount() === 0) {
            <cp-notice role="note">You haven’t kept a post yet. The card can still go on the board and be filled in later.</cp-notice>
          }
          @if (problem(); as sentence) {
            <cp-notice tone="error" role="alert">{{ sentence }}</cp-notice>
          }
          @if (canAdd()) {
            <div class="cp-actions">
              <button cpButton [busy]="adding()" type="button" variant="primary" [attr.aria-disabled]="adding() || null" (click)="add()">
                {{ adding() ? 'Adding…' : 'Add to your board' }}
              </button>
            </div>
          } @else {
            <cp-notice role="note">You have view-only access, so you can’t add cards to the board. Ask an Owner or Editor.</cp-notice>
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

    .plain {
      margin: 0;
    }

    .loading {
      display: grid;
      gap: var(--cp-space-3);
    }
  `,
})
export class SocialBoardStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly board = inject(ContentBoardService);
  private readonly contexts = inject(CreativeContextService);
  private readonly posts = inject(ChannelPostService);
  private readonly port = inject(WorkflowStepPort);
  private readonly memberships = inject(WorkspaceMembershipService);

  protected readonly read = signal<Read>({ status: 'loading' });
  protected readonly keepFailed = signal(false);

  /** A Viewer reads the work and is not offered the one write here, which the server refuses them. */
  protected readonly canAdd = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && role !== 'Viewer';
  });
  protected readonly card = signal<ContentCard | null>(null);
  protected readonly adding = signal(false);
  protected readonly problem = signal<string | null>(null);

  private gone = false;

  private readonly ready = computed(() => {
    const read = this.read();
    return read.status === 'ready' ? read : null;
  });

  protected readonly keptCount = computed(
    () => this.ready()?.posts?.channels.filter((channel) => channel.status === 'Accepted').length ?? 0,
  );

  protected readonly keptText = computed(() => {
    const count = this.keptCount();
    return count === 0 ? 'None yet' : count === 1 ? 'One' : `${count}`;
  });

  protected readonly pictureText = computed(() =>
    pictureReferenceOf(this.ready()?.context ?? null)?.mediaAssetId ? 'The one you chose' : 'None',
  );

  /** The work's own name, else a plain one: the card is the creator's to rename on the board. */
  protected readonly cardTitle = computed(() => this.ready()?.context.workingTitle?.trim() || 'Social posts');

  private readonly cardPointer = computed<WorkflowResourcePointer | null>(() => {
    const card = this.card();
    return card === null ? null : { kind: 'ContentCard', id: card.id };
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    // The card is made by a command the server answers; there is nothing to keep before a move.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      saving: this.adding.asReadonly(),
      flush: () => Promise.resolve(true),
      draft: this.cardPointer,
      results: computed(() => {
        const pointer = this.cardPointer();
        return pointer === null ? [] : [pointer];
      }),
      ready: computed(() => this.card() !== null),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.memberships.ensureLoaded();
    void this.load();
  }

  protected columnLabel(card: ContentCard): string {
    return CONTENT_BOARD_COLUMN_LABELS[card.column];
  }

  async load(): Promise<void> {
    const slug = this.workspaceSlug();
    const run = this.run();

    // A card the run already holds is shown, never offered again.
    const cardId =
      newestLinked(run, 'ContentCard') ?? run.steps.find((step) => step.draft?.kind === 'ContentCard')?.draft?.id ?? null;
    if (cardId !== null) {
      this.read.set({ status: 'loading' });
      const held = await firstValueFrom(this.board.detail(slug, cardId));
      if (this.gone) return;
      if (held.status === 'found') {
        this.card.set(held.detail.card);
        return;
      }
      // Only a card that is gone is offered again. One that could not be read just now is still there.
      if (held.status !== 'not_found') {
        this.read.set({ status: 'unavailable' });
        return;
      }
    }

    const contextId = runContextId(run);
    if (contextId === null) {
      this.read.set({ status: 'no-work' });
      return;
    }

    this.read.set({ status: 'loading' });
    const [context, posts] = await Promise.all([
      firstValueFrom(this.contexts.get(slug, contextId)),
      this.posts.readPackage(slug, contextId),
    ]);
    if (this.gone) return;

    if (context.status !== 'found') {
      this.read.set({ status: context.status === 'not_found' ? 'no-work' : 'unavailable' });
      return;
    }

    this.read.set({ status: 'ready', context: context.context, posts: posts.status === 'found' ? posts.package : null });
  }

  protected async add(): Promise<void> {
    const ready = this.ready();
    if (ready === null || this.adding() || !this.canAdd()) return;

    const { context, posts } = ready;
    const recipe = linkedRecipeOf(context);
    const picture = pictureReferenceOf(context);
    const key = this.requestKey();

    this.adding.set(true);
    this.problem.set(null);

    const outcome = await firstValueFrom(
      this.board.create(
        this.workspaceSlug(),
        { title: this.cardTitle(), description: null, channelKey: null, plannedDate: null },
        'ToDo',
        key,
        {
          creativeContextId: context.id,
          recipeId: recipe?.recipeId ?? null,
          recipeVersionId: recipe?.recipeVersionId ?? null,
          mediaAssetId: picture?.mediaAssetId ?? null,
          mediaAssetVersionNumber: picture?.mediaAssetId ? (picture.mediaAssetVersionNumber ?? null) : null,
          socialPackageId: posts?.id ?? null,
        },
      ),
    );
    if (this.gone) return;
    this.adding.set(false);

    if (outcome.status === 'saved') {
      this.forgetKey();
      this.card.set(outcome.detail.card);
      void this.port.keepDraft({ kind: 'ContentCard', id: outcome.detail.card.id }).then((kept) => {
        if (!this.gone) this.keepFailed.set(!kept);
      });
      this.port.announce(`Added to your board as “${outcome.detail.card.title}”.`);
      return;
    }

    // A key reused for a different card is spent; the next press is a new request.
    if (outcome.status === 'key_reused') this.forgetKey();
    this.problem.set(PROBLEMS[outcome.status] ?? PROBLEMS['unavailable']);
  }

  /** The key of the one card this run makes, kept in this browser's session so a reload sends the same request. */
  private requestKey(): string {
    const name = this.keyName();
    try {
      const kept = sessionStorage.getItem(name);
      if (kept) return kept;

      const fresh = crypto.randomUUID();
      sessionStorage.setItem(name, fresh);
      return fresh;
    } catch {
      return (this.fallbackKey ??= crypto.randomUUID());
    }
  }

  private fallbackKey: string | null = null;

  /** Scoped by workspace as well as run, so nothing kept for one workspace is ever read in another. */
  private keyName(): string {
    return `${KEY_PREFIX}${this.workspaceSlug()}:${this.run().id}`;
  }

  private forgetKey(): void {
    this.fallbackKey = null;
    try {
      sessionStorage.removeItem(this.keyName());
    } catch {
      // Nothing kept, so nothing to forget.
    }
  }
}
