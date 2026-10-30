import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest, firstValueFrom, map } from 'rxjs';
import {
  CpBackLinkComponent,
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpSkeletonComponent,
  CpStatusPillComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { injectRouteTitle } from '../../core/route-title';
import { ContentChannel } from '../../models/brand-profile.models';
import {
  CONTENT_BOARD_COLUMN_LABELS,
  ContentCardDetail,
  ContentCardLineageEntry,
} from '../../models/content-board.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ContentBoardService } from '../../services/content-board.service';
import { HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';
import { IdempotencyKey } from '../content-pipeline/content-pipeline-idempotency';
import { channelNameOf, lineageNounOf } from './content-board-presentation';
import { ContentCardEditDrawerComponent } from './content-card-edit-drawer.component';
import { ContentCardPublishingComponent } from './content-card-publishing.component';
import { PublishingTone } from './publishing-presentation';

type State =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly detail: ContentCardDetail }
  | { readonly status: 'not_found' }
  | { readonly status: 'deleted'; readonly title: string }
  | { readonly status: 'error' };

/** One thing a card points at, with where a creator can go to see it. */
interface LineageRow {
  readonly entry: ContentCardLineageEntry;
  readonly noun: string;
  /** What is said about it: its own name where the server could read one, or what kind of thing it is. */
  readonly text: string;
  readonly pinned: string;
  readonly link: readonly string[] | null;
}

const DELETE_PROBLEMS = {
  stale:
    'This card changed since you opened it, so it was not deleted. It has been read again; delete it again if you still want to.',
  forbidden: 'You do not have permission to delete cards in this workspace. Nothing was deleted.',
  unavailable: 'The card could not be deleted just now. Nothing was deleted, so it is safe to try again.',
} as const;

/**
 * One card of the content board in full (`:workspaceSlug/content-board/:cardId`, BOARD-UI-002/004/005): what it
 * says, what it points at, where it has been, and where it stands with publishing.
 *
 * **A detail page**, in that shape's order: one head for every state, one fixed place for messages, then a
 * column of topics with each action under the topic it acts on, and the action that takes the card away last.
 *
 * **Lineage is links, never copies.** Each record the card points at is named by the server and opened on its
 * own page; a record that can no longer be read is still listed, and said to be unavailable, rather than
 * silently dropped.
 *
 * **Posts are written in Social Studio.** A card that came from a piece of work offers the way there; this
 * page writes none.
 *
 * **Publishing is said by the server, and done through it.** The Publishing topic reads this card's
 * publications, offers the one way to confirm another, and offers a cancellation for those that have not gone
 * out. Nothing on this page declares a publication scheduled, published or cancelled before the server has
 * said so — an outcome nobody knows yet is shown as exactly that.
 *
 * **Deleting keeps the picture.** A card's linked picture is never removed with it, and the confirmation says
 * so. The request carries one idempotency key across retries, so a delete whose answer was lost is told what
 * happened instead of failing on a card that is already gone.
 */
@Component({
  selector: 'cp-content-card-detail',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    CpBackLinkComponent,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpSkeletonComponent,
    CpStatusPillComponent,
    CpTopicCardComponent,
    ContentCardEditDrawerComponent,
    ContentCardPublishingComponent,
  ],
  templateUrl: './content-card-detail.component.html',
  styleUrl: './content-card-detail.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentCardDetailComponent {
  private readonly board = inject(ContentBoardService);
  private readonly brand = inject(BrandProfileService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);

  readonly workspaceSlug = signal('');
  private readonly cardId = signal('');

  protected readonly state = signal<State>({ status: 'loading' });
  protected readonly channels = signal<readonly ContentChannel[]>([]);

  /** What the page just did. Always in the DOM, so it is announced reliably. */
  protected readonly notice = signal('');
  protected readonly problem = signal('');

  protected readonly editing = signal(false);
  protected readonly deleting = signal(false);

  private editOpener: HTMLElement | null = null;
  private readonly deleteKey = new IdempotencyKey();

  protected readonly detail = computed(() => {
    const state = this.state();

    return state.status === 'ready' ? state.detail : null;
  });

  protected readonly headTitle = computed(() => {
    const state = this.state();

    switch (state.status) {
      case 'ready':
        return state.detail.card.title;
      case 'deleted':
        return 'Card deleted';
      case 'not_found':
        return 'Card not found';
      // While it loads, the route's own title: the title slot never holds a loading sentence (AU.7).
      default:
        return this.routeTitle;
    }
  });

  /** The route's own title, as the DAM's picture page reads its own: so the two cannot drift. */
  private readonly routeTitle = injectRouteTitle('Card');

  protected readonly boardLink = computed(() => ['/', this.workspaceSlug(), 'content-board']);
  protected readonly columnLabel = computed(() => {
    const detail = this.detail();

    return detail === null ? '' : CONTENT_BOARD_COLUMN_LABELS[detail.card.column];
  });
  /**
   * What the head badge says about publishing: the publications' own summary, raised by the topic that read
   * them.
   *
   * **Not the card's own counts.** The card carries a publishing summary the server does not fill, and a badge
   * drawn from it would say "Not scheduled" over a card with a post going out this afternoon.
   */
  protected readonly publishingSummary = signal<{ label: string; tone: PublishingTone }>({
    label: 'Not scheduled',
    tone: 'neutral',
  });

  protected readonly publishing = computed(() => this.publishingSummary());
  protected readonly channelName = computed(() =>
    channelNameOf(this.channels(), this.detail()?.card.channelKey ?? null),
  );

  protected readonly lineage = computed<readonly LineageRow[]>(() =>
    (this.detail()?.lineage ?? []).map((entry) => {
      const noun = lineageNounOf(entry);

      return {
        entry,
        noun,
        text: entry.label?.trim() || this.unnamed(entry),
        pinned:
          entry.kind === 'MediaAsset' && entry.versionNumber !== null
            ? `Version ${entry.versionNumber}`
            : entry.kind === 'Recipe' && entry.versionId !== null
              ? 'One exact version'
              : '',
        link: entry.available ? this.linkFor(entry) : null,
      };
    }),
  );

  /** Where the card's posts are written, when it came from a piece of work. */
  protected readonly postsLink = computed(() => {
    const contextId = this.detail()?.card.lineage.creativeContextId ?? null;

    return contextId === null ? null : ['/', this.workspaceSlug(), ...HANDOFF_ROUTES.socialStudio(contextId)];
  });

  protected readonly hasPosts = computed(() => this.detail()?.card.lineage.socialPackageId != null);

  protected columnName(column: keyof typeof CONTENT_BOARD_COLUMN_LABELS | null): string {
    return column === null ? '' : CONTENT_BOARD_COLUMN_LABELS[column];
  }

  constructor() {
    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          let slug: string | null = null;
          let id: string | null = null;
          for (const params of maps) {
            slug ??= params.get('workspaceSlug');
            id ??= params.get('cardId');
          }

          return { slug, id };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, id }) => {
        if (!slug || !id) throw new Error('ContentCardDetailComponent route is missing a workspaceSlug or cardId.');

        // Another card, or another workspace: nothing about the last one stays on screen.
        this.workspaceSlug.set(slug);
        this.cardId.set(id);
        this.notice.set('');
        this.problem.set('');
        this.editing.set(false);
        this.deleteKey.clear();
        this.state.set({ status: 'loading' });
        void this.read();
      });

    void this.loadChannels();
  }

  protected retry(): void {
    this.state.set({ status: 'loading' });
    void this.read();
  }

  // ---- Editing ----

  protected openEdit(event: Event): void {
    this.editOpener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    // A drawer gives focus back, on close, to whatever held it when it opened. A click does not focus a
    // button in every browser, so the button is focused here rather than left to chance.
    this.editOpener?.focus();
    this.notice.set('');
    this.problem.set('');
    this.editing.set(true);
  }

  protected closeEdit(): void {
    this.editing.set(false);

    const opener = this.editOpener;
    this.editOpener = null;
    if (opener?.isConnected) opener.focus();
  }

  protected onSaved(detail: ContentCardDetail): void {
    this.state.set({ status: 'ready', detail });
    this.closeEdit();
    this.notice.set('Your changes were saved.');
  }

  /** The edit found the card changed: read it again, and leave the drawer open with what was typed. */
  protected onEditStale(): void {
    void this.read();
  }

  protected onGone(): void {
    this.editing.set(false);
    this.state.set({ status: 'not_found' });
  }

  // ---- Deleting ----

  protected async remove(): Promise<void> {
    const detail = this.detail();
    if (detail === null || this.deleting()) return;

    const hasPicture = detail.card.lineage.mediaAssetId !== null;
    const agreed = await this.confirm.confirm({
      title: `Delete “${detail.card.title}”?`,
      message: hasPicture
        ? 'The card and its history are deleted for everyone in this workspace. Its picture stays in your library, and its recipe, prompt and posts are not touched.'
        : 'The card and its history are deleted for everyone in this workspace. Nothing it points at is touched.',
      confirmLabel: 'Delete card',
      cancelLabel: 'Keep it',
      tone: 'danger',
    });
    if (!agreed) return;

    const slug = this.workspaceSlug();
    const id = this.cardId();

    this.deleting.set(true);
    this.notice.set('');
    this.problem.set('');

    const outcome = await firstValueFrom(
      this.board.remove(slug, id, detail.card.concurrencyToken, this.deleteKey.next()),
    );
    // Cleared before the check below: an answer for a workspace or card no longer on screen must not leave
    // this busy for good.
    this.deleting.set(false);
    if (this.workspaceSlug() !== slug || this.cardId() !== id) return;

    switch (outcome.status) {
      case 'deleted':
      // Already gone is what the creator asked for, whoever did it.
      case 'not_found':
        this.deleteKey.clear();
        this.state.set({ status: 'deleted', title: detail.card.title });
        return;

      case 'stale':
        // A different card now, so a different request: the old key must not replay the old refusal.
        this.deleteKey.clear();
        this.problem.set(DELETE_PROBLEMS.stale);
        await this.read();
        return;

      default:
        this.problem.set(DELETE_PROBLEMS[outcome.status]);
    }
  }

  // ---- Reading ----

  private async read(): Promise<void> {
    const slug = this.workspaceSlug();
    const id = this.cardId();

    const outcome = await firstValueFrom(this.board.detail(slug, id));
    if (this.workspaceSlug() !== slug || this.cardId() !== id) return;

    if (outcome.status === 'found') {
      this.state.set({ status: 'ready', detail: outcome.detail });
    } else if (this.state().status !== 'ready' || outcome.status === 'not_found') {
      // A refresh that failed leaves the card that is on screen where it is.
      this.state.set({ status: outcome.status === 'not_found' ? 'not_found' : 'error' });
    }
  }

  private async loadChannels(): Promise<void> {
    const outcome = await this.brand.listContentChannels();
    // Without them a channel is shown by its key, which is still true; nothing else on the page waits.
    if (outcome.status === 'found') this.channels.set(outcome.channels);
  }

  private linkFor(entry: ContentCardLineageEntry): readonly string[] | null {
    const slug = this.workspaceSlug();

    switch (entry.kind) {
      case 'Recipe':
        return ['/', slug, 'recipes', entry.id];
      case 'MediaAsset':
        return ['/', slug, 'dam', entry.id];
      case 'PromptRecord':
        return ['/', slug, 'prompt-library', entry.id];
      default:
        // A piece of work has no page of its own, and its posts are opened from the Posts topic below.
        return null;
    }
  }

  private unnamed(entry: ContentCardLineageEntry): string {
    switch (entry.kind) {
      case 'MediaAsset':
        return 'A picture in your library';
      case 'SocialPackage':
        return 'The posts written for this work';
      case 'CreativeContext':
        return 'An untitled piece of work';
      case 'PromptRecord':
        return 'A saved prompt';
      default:
        return 'A recipe';
    }
  }

  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}
