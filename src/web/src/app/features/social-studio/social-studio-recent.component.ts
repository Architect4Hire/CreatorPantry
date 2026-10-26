import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  CpButtonComponent,
  CpCardComponent,
  CpCardGridComponent,
  CpEmptyStateComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import { ChannelPostChannel, ChannelPostPackage } from '../../models/channel-post.models';
import { CreativeContextSummary } from '../../models/creative-context.models';
import { ChannelPostService } from '../../services/channel-post.service';
import { CreativeContextService } from '../../services/creative-context.service';
import { HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';

/** How many recent pieces of work are looked through for posts. One page, and each is one more read. */
export const SOCIAL_STUDIO_RECENT_LIMIT = 10;

/** One piece of work that has posts, as a row of the list. */
interface RecentSet {
  readonly contextId: string;
  /** The creator's own name for the piece, or null when they gave it none. */
  readonly title: string | null;
  readonly channelNames: string;
  readonly day: string | null;
  readonly updatedAt: string;
  readonly standing: { readonly word: string; readonly tone: CpStatusPillTone };
  readonly link: readonly string[];
}

type State =
  | { readonly status: 'loading' }
  /** `partial` when some pieces of work could not be looked at, so the list may be short. */
  | { readonly status: 'ready'; readonly sets: readonly RecentSet[]; readonly partial: boolean; readonly more: boolean }
  | { readonly status: 'unavailable' };

/**
 * Where a set of posts stands, in one phrase.
 *
 * The thing a creator most needs from a list is which sets still want them, so that is said first: a channel
 * needing another look outranks one still waiting, which outranks everything being settled.
 */
function standingOf(channels: readonly ChannelPostChannel[]): RecentSet['standing'] {
  const count = (status: ChannelPostChannel['status']): number =>
    channels.filter((channel) => channel.status === status).length;

  const review = count('NeedsReview');
  if (review > 0) return { word: review === 1 ? '1 needs another look' : `${review} need another look`, tone: 'stale' };

  const waiting = count('Proposed');
  if (waiting > 0) return { word: waiting === 1 ? '1 waiting for you' : `${waiting} waiting for you`, tone: 'progress' };

  const accepted = count('Accepted');
  if (accepted > 0) return { word: accepted === channels.length ? 'All accepted' : `${accepted} accepted`, tone: 'success' };

  return { word: 'None used', tone: 'neutral' };
}

/**
 * The workspace's recent sets of posts, so one can be opened again (master 13.3).
 *
 * **There is no route that lists posts.** What exists is the workspace's recent pieces of work and, for any one
 * of them, its posts — so this reads the first page of the former and looks at each for the latter, and shows
 * the ones that have some. Both reads are ones the Content Pipeline already makes, under the same Viewer-level
 * policy and the same workspace filter; nothing here reads anything a member could not already open.
 *
 * **It prints no creator content it has no summary for.** A row is the creator's own working title, the
 * channels, the day, when it last changed and where its posts stand. No post body, no brief, no recipe text —
 * and a piece with no title is called exactly that rather than named after something read out of a post.
 *
 * **It is a convenience and fails like one.** If the list cannot be read the page above it is untouched; if
 * only some pieces could be looked at, it says the list may be short rather than passing a part off as the whole.
 */
@Component({
  selector: 'cp-social-studio-recent',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpCardGridComponent,
    CpEmptyStateComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
    CpStatusPillComponent,
  ],
  template: `
    <!-- Always in the page, so it is announced when its words change; the skeleton under it is aria-hidden. -->
    <p class="cp-sr-only" role="status">{{ state().status === 'loading' ? 'Looking for posts you have written…' : '' }}</p>

    @switch (state().status) {
      @case ('loading') {
        <div aria-busy="true">
          <cp-skeleton shape="card" [count]="3" />
        </div>
      }
      @case ('unavailable') {
        <cp-notice tone="warning" role="status">
          Your recent posts couldn't be listed right now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (partial()) {
          <cp-notice tone="warning" role="status">
            Some of your recent work couldn't be looked at, so this list may be missing a set.
            <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
          </cp-notice>
        }

        @if (sets().length === 0) {
          <cp-empty-state
            title="No posts yet"
            [description]="more() ? 'None of your most recent work has posts yet.' : 'Nothing has posts yet. The first set you write will be listed here.'"
            illustration="post"
          />
        } @else {
          <ul cpCardGrid minColumn="18rem" aria-label="Recent sets of posts">
            @for (set of sets(); track set.contextId) {
              <li>
                <cp-card>
                  <div class="set">
                    <h3 class="title">
                      <a [routerLink]="set.link" [attr.aria-current]="set.contextId === currentContextId() ? 'page' : null">
                        {{ set.title ?? 'Untitled piece of work' }}
                      </a>
                    </h3>
                    <p class="standing">
                      <cp-status-pill [tone]="set.standing.tone">{{ set.standing.word }}</cp-status-pill>
                    </p>
                    <dl cpFacts layout="inline">
                      <div cpFact label="Channels">{{ set.channelNames }}</div>
                      @if (set.day; as day) {
                        <div cpFact label="Day">{{ day }}</div>
                      }
                      <div cpFact label="Last changed">
                        <time [attr.datetime]="set.updatedAt">{{ dateOf(set.updatedAt) }}</time>
                      </div>
                    </dl>
                  </div>
                </cp-card>
              </li>
            }
          </ul>

          @if (more()) {
            <p class="cp-muted">Showing posts from your {{ limit }} most recent pieces of work.</p>
          }
        }
      }
    }
  `,
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-4);
      }

      li {
        min-width: 0;
      }

      .set {
        display: grid;
        gap: var(--cp-space-3);
        align-content: start;
      }

      .title,
      .standing {
        margin: 0;
      }

      .title {
        font-family: var(--cp-font-display);
        font-size: var(--cp-font-size-lg);
        overflow-wrap: anywhere;
      }

      .title a {
        color: var(--cp-primary);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SocialStudioRecentComponent {
  private readonly contexts = inject(CreativeContextService);
  private readonly posts = inject(ChannelPostService);

  readonly workspaceSlug = input.required<string>();
  /** Every channel the product knows, for naming. A key with no name here is shown as its key. */
  readonly channels = input<readonly ContentChannel[]>([]);
  /** The piece of work open on the page, so its own row can say it is the one on screen. */
  readonly currentContextId = input<string | null>(null);

  protected readonly limit = SOCIAL_STUDIO_RECENT_LIMIT;

  /** What was read: each piece of work that has posts, with the package that says so. */
  private readonly found = signal<
    | { readonly status: 'loading' }
    | {
        readonly status: 'ready';
        readonly rows: readonly { readonly summary: CreativeContextSummary; readonly package: ChannelPostPackage }[];
        readonly partial: boolean;
        readonly more: boolean;
      }
    | { readonly status: 'unavailable' }
  >({ status: 'loading' });

  protected readonly state = computed<State>(() => {
    const found = this.found();
    if (found.status !== 'ready') return found;

    const names = new Map(this.channels().map((channel) => [channel.key, channel.displayName]));
    const slug = this.workspaceSlug();

    return {
      status: 'ready',
      partial: found.partial,
      more: found.more,
      sets: found.rows.map(({ summary, package: posts }) => ({
        contextId: summary.id,
        title: summary.workingTitle?.trim() || null,
        channelNames: posts.channels.map((channel) => names.get(channel.channelKey) ?? channel.channelKey).join(', '),
        day: summary.day,
        // The posts' own date, not the work's: a set is "recent" by when its words last changed.
        updatedAt: posts.updatedAt,
        standing: standingOf(posts.channels),
        link: ['/', slug, ...HANDOFF_ROUTES.socialStudio(summary.id)],
      })),
    };
  });

  protected readonly sets = computed(() => {
    const state = this.state();
    return state.status === 'ready' ? state.sets : [];
  });

  protected readonly partial = computed(() => {
    const state = this.state();
    return state.status === 'ready' && state.partial;
  });

  protected readonly more = computed(() => {
    const state = this.state();
    return state.status === 'ready' && state.more;
  });

  /** The workspace the list was read for, so the same one is not read twice. */
  private loadedSlug: string | null = null;
  private gone = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.gone = true));

    // Keyed on the workspace, so a page reused for another one cannot go on showing the first one's work
    // (.claude/rules/tenancy.md).
    effect(() => {
      const slug = this.workspaceSlug();
      if (slug === this.loadedSlug) return;

      this.loadedSlug = slug;
      untracked(() => void this.load(slug));
    });
  }

  protected retry(): void {
    void this.load(this.workspaceSlug());
  }

  protected dateOf(iso: string): string {
    const at = new Date(iso);

    return Number.isNaN(at.getTime()) ? '' : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(at);
  }

  private async load(slug: string): Promise<void> {
    this.found.set({ status: 'loading' });

    const listed = await firstValueFrom(this.contexts.list(slug, null, SOCIAL_STUDIO_RECENT_LIMIT));
    if (this.stale(slug)) return;

    if (listed.status !== 'found') {
      this.found.set({ status: 'unavailable' });
      return;
    }

    const read = await Promise.all(
      listed.page.items.map(async (summary) => ({ summary, outcome: await this.posts.readPackage(slug, summary.id) })),
    );
    if (this.stale(slug)) return;

    const rows = read.flatMap(({ summary, outcome }) =>
      outcome.status === 'found' && outcome.package !== null && outcome.package.channels.length > 0
        ? [{ summary, package: outcome.package }]
        : [],
    );

    this.found.set({
      status: 'ready',
      rows: rows.sort((left, right) => right.package.updatedAt.localeCompare(left.package.updatedAt)),
      // A piece archived or removed between the two reads is simply not there; anything else unread is a gap.
      partial: read.some(({ outcome }) => outcome.status === 'unavailable' || outcome.status === 'forbidden'),
      more: listed.page.nextCursor !== null,
    });
  }

  /** True when the answer in hand is for a workspace the page has left, or the page itself has gone. */
  private stale(slug: string): boolean {
    return this.gone || slug !== this.loadedSlug;
  }
}
