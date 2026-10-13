import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpStatusPillComponent,
} from '@creator-pantry/ui';

import {
  CONTENT_PIPELINE_STEPS,
  CONTENT_PIPELINE_STEP_COUNT,
  ContentPipelineDraft,
  FIRST_CONTENT_PIPELINE_STEP,
  contentPipelineStepIndex,
  isContentPipelineDraftEmpty,
} from '../../models/content-pipeline.models';
import {
  ContentPipelineDraftOwner,
  ContentPipelineDraftService,
} from '../../services/content-pipeline-draft.service';
import { LongFormDraftSummary, LongFormKind } from '../../models/long-form-draft.models';
import { LongFormDraftStatus } from '../../models/long-form-draft.models';
import {
  LONG_FORM_PIPELINE_STEPS,
  LongFormPipelineStepSlug,
  longFormPipelineStepIndex,
} from '../../models/long-form-pipeline.models';
import { LongFormDraftService } from '../../services/long-form-draft.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';

/** How many long-form pieces in progress the card names. More than that is a library, and there is none yet. */
export const WORKFLOWS_HUB_LONG_FORM_ROWS = 3;

/** How far back the card looks for them: the workspace's most recently changed drafts. */
const LONG_FORM_LOOKBACK = 20;

const LONG_FORM_KINDS: Readonly<Record<LongFormKind, string>> = {
  BlogArticle: 'Blog article',
  NewsletterIssue: 'Newsletter issue',
};

/**
 * The step a piece is *at*, for telling a creator where they left it. Not the furthest step the pipeline would
 * let them open — a piece being written may be read whole, but it is being written, and that is what to say.
 */
function longFormStepFor(status: LongFormDraftStatus): LongFormPipelineStepSlug {
  switch (status) {
    case 'Draft':
      return 'outline';
    case 'Outlined':
    case 'Writing':
      return 'write';
    default:
      return 'review';
  }
}

/** The long-form pieces this workspace has in progress, as the server says: not read yet, read, or not readable. */
type LongFormRead =
  | { readonly state: 'loading' }
  | { readonly state: 'read'; readonly pieces: readonly LongFormDraftSummary[] }
  | { readonly state: 'unavailable' };

interface LongFormRow {
  readonly id: string;
  readonly kind: string;
  /** True for a piece accepted and since marked for review: it is finished, and wants looking at. */
  readonly needsReview: boolean;
  readonly step: string;
  readonly when: string;
  readonly link: readonly string[];
  readonly action: string;
  readonly actionLabel: string;
}

/**
 * `loading` and `unavailable` are how reading the creator's memberships can end; `not_found` is the one
 * refusal; `read_only` and `ready` both show the card, with and without a way in.
 */
type Phase = 'loading' | 'unavailable' | 'not_found' | 'read_only' | 'ready';

/**
 * The Workflows page: the guided journeys that exist, and a way into each (FLOW-004).
 *
 * **A stopgap, and small enough to delete.** There are three journeys today, so there are three cards, written
 * out rather than driven from a list: no catalogue, no run entity and no shared shell. The real Workflow Hub
 * replaces this file whole, and a list abstraction built for three entries would only be something else to
 * remove. Of the three things FLOW-004 names, this shows a journey in progress; "recently completed" and
 * "needs attention" need a run record that does not exist yet.
 *
 * **Each card says which kind of work it is for** (LF.6.3, B-38), in a badge and a sentence, so the short-form
 * pipeline and the long-form one are told apart at a glance and neither is mistaken for a step of the other.
 *
 * **The Content Pipeline's card asks the server nothing.** Whether there is something to continue is read from
 * the creator's own kept draft, by workspace id and membership id, which is what keeps another workspace's —
 * or a colleague's — unfinished work off this card (.claude/rules/tenancy.md).
 *
 * **The long-form card asks the server, and only the server** (LF.6.3). A long-form piece's plan, its text and
 * where it stands are all on its draft, so what is in progress is the workspace's drafts as the list route
 * returns them under the route's workspace: nothing about a long-form piece is read from this device. That
 * read is the card's own — it does not hold up the other two, and when it fails the card says so and still
 * offers a way to start. The list carries a kind, a status and a date, and no word of any piece, so a row says
 * those and no title.
 *
 * **Only destinations that exist are offered.** A long-form piece can be started from a recipe or from a brief,
 * and both open the pipeline's first step; there is no long-form library to send anyone to, so at most
 * {@link WORKFLOWS_HUB_LONG_FORM_ROWS} pieces are named and nothing claims to list them all.
 *
 * **Reading a draft that cannot be read throws it away**, which is `ContentPipelineDraftService`'s behaviour
 * and not this page's choice. So this is the one place a creator is told: the pipeline, opened next, finds
 * nothing and has nothing to report. This page is the way in, so the notice lands where they are.
 *
 * **Continue goes to the pipeline, which offers "pick up where you left off" itself.** Starting over lives
 * there and only there; a second copy here would be a second way to throw the draft away.
 *
 * An unknown workspace and one this creator is not in answer alike, so neither discloses the other.
 */
@Component({
  selector: 'cp-workflows-hub',
  standalone: true,
  imports: [
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpStatusPillComponent,
  ],
  template: `
    <cp-page-head
      heading="Workflows"
      lede="Guided, step-by-step ways to get something made. Stop whenever you like and pick up later."
    />

    <div class="hub">

      @switch (phase()) {
        @case ('loading') {
          <cp-card><p class="plain" role="status">Loading your workflows…</p></cp-card>
        }
        @case ('unavailable') {
          <cp-card>
            <cp-notice tone="error" role="alert">
              Your workspaces couldn't be loaded right now.
              <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
            </cp-notice>
          </cp-card>
        }
        @case ('not_found') {
          <cp-card><cp-notice tone="error" role="alert">We couldn't find this workspace.</cp-notice></cp-card>
        }
        @default {
          <cp-card>
            <article class="journey" aria-labelledby="workflow-pipeline-heading">
              <div class="title">
                <h2 id="workflow-pipeline-heading">Content Pipeline</h2>
                <cp-badge tone="pink">Short-form</cp-badge>
                @if (resume()) {
                  <cp-status-pill tone="progress">In progress</cp-status-pill>
                }
              </div>
              <p class="plain">From an idea to pictures and posts, one step at a time.</p>
              <p class="plain cp-muted">For short-form work: a picture and the posts that go out with it.</p>

              @if (phase() === 'read_only') {
                <cp-notice role="note">
                  You have view-only access to this workspace, so you can't run this. Ask an Owner or Editor to run
                  it for you.
                </cp-notice>
              } @else {
                @if (discarded()) {
                  <cp-notice tone="warning" role="status">
                    Something was kept here before, but it couldn't be read — so this is a fresh start.
                  </cp-notice>
                }

                @if (resume(); as kept) {
                  <p class="plain">
                    You were on <strong>{{ kept.label }}</strong>, step {{ kept.number }} of {{ total }}.
                    @if (kept.when) {
                      You last worked on it here {{ kept.when }}.
                    }
                  </p>
                  <div class="cp-actions">
                    <a cpButton variant="primary" [routerLink]="kept.link" aria-label="Continue the Content Pipeline">
                      Continue
                    </a>
                  </div>
                } @else {
                  <div class="cp-actions">
                    <a cpButton variant="primary" [routerLink]="startLink()" aria-label="Start the Content Pipeline">
                      Start
                    </a>
                  </div>
                }
              }
            </article>
          </cp-card>

          <cp-card>
            <article class="journey" aria-labelledby="workflow-long-form-heading">
              <div class="title">
                <h2 id="workflow-long-form-heading">Write a blog post or newsletter</h2>
                <cp-badge tone="purple">Long-form</cp-badge>
              </div>
              <p class="plain">
                A full article or a newsletter issue, from a plan you approve to a piece you accept. One step at a
                time.
              </p>
              <p class="plain cp-muted">For long-form work: one piece to read, not a post.</p>

              @if (phase() === 'read_only') {
                <cp-notice role="note">
                  You have view-only access to this workspace, so you can't run this. Ask an Owner or Editor to run
                  it for you.
                </cp-notice>
              } @else {
                @switch (longForm().state) {
                  @case ('loading') {
                    <p class="plain cp-muted" role="status" data-testid="long-form-loading">
                      Checking for pieces you have in progress…
                    </p>
                  }
                  @case ('unavailable') {
                    <cp-notice tone="warning" role="status" data-testid="long-form-unavailable">
                      Your pieces in progress couldn't be loaded right now. You can still start a new one.
                      <button cpButton type="button" variant="secondary" size="sm" (click)="retryLongForm()">Try again</button>
                    </cp-notice>
                  }
                  @case ('read') {
                    @if (longFormRows().length > 0) {
                      <h3 id="workflow-long-form-progress">In progress</h3>
                      <ul class="pieces" aria-labelledby="workflow-long-form-progress" data-testid="long-form-pieces">
                        @for (row of longFormRows(); track row.id) {
                          <li>
                            <span>
                              {{ row.kind }}
                              @if (row.needsReview) {
                                <cp-status-pill tone="warning">Needs your review</cp-status-pill>
                              } @else {
                                <span class="cp-muted">· on {{ row.step }}</span>
                              }
                              @if (row.when) {
                                <span class="cp-muted">· last changed {{ row.when }}</span>
                              }
                            </span>
                            <a cpButton variant="secondary" size="sm" [routerLink]="row.link" [attr.aria-label]="row.actionLabel">
                              {{ row.action }}
                            </a>
                          </li>
                        }
                      </ul>
                    }
                  }
                }

                <div class="cp-actions">
                  <a
                    cpButton
                    variant="primary"
                    [routerLink]="longFormLink()"
                    [queryParams]="{ from: 'recipe' }"
                    aria-label="Start a blog post or newsletter from a recipe"
                  >
                    Start from a recipe
                  </a>
                  <a
                    cpButton
                    variant="secondary"
                    [routerLink]="longFormLink()"
                    [queryParams]="{ from: 'brief' }"
                    aria-label="Start a blog post or newsletter from a brief"
                  >
                    Start from a brief
                  </a>
                </div>
              }
            </article>
          </cp-card>

          <cp-card>
            <article class="journey" aria-labelledby="workflow-quick-recipe-heading">
              <div class="title">
                <h2 id="workflow-quick-recipe-heading">Quick Recipe</h2>
                <cp-badge tone="orange">Recipe</cp-badge>
              </div>
              <p class="plain">Name a dish and get a draft recipe and a lead picture for it, saved together.</p>
              <p class="plain cp-muted">For a new recipe: a draft to work on in your library.</p>

              @if (phase() === 'read_only') {
                <cp-notice role="note">
                  You have view-only access to this workspace, so you can't run this. Ask an Owner or Editor to run
                  it for you.
                </cp-notice>
              } @else {
                <div class="cp-actions">
                  <a cpButton variant="primary" [routerLink]="quickRecipeLink()" aria-label="Start a Quick Recipe">
                    Start
                  </a>
                </div>
              }
            </article>
          </cp-card>
        }
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .hub,
      .journey {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
      }
      .hub {
        gap: var(--cp-space-6);
      }
      .journey {
        gap: var(--cp-space-3);
        max-width: var(--cp-measure-prose);
      }
      h2 {
        margin: 0;
        font-family: var(--cp-font-display);
      }
      h3 {
        margin: 0;
        font-size: var(--cp-font-size-md);
      }
      .pieces {
        display: grid;
        gap: var(--cp-space-2);
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .pieces li {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--cp-space-2) var(--cp-space-3);
      }
      .title {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--cp-space-3);
      }
      .plain {
        margin: 0;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkflowsHubComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly drafts = inject(ContentPipelineDraftService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly longFormDrafts = inject(LongFormDraftService);

  protected readonly total = CONTENT_PIPELINE_STEP_COUNT;

  /** The workspace's long-form drafts as the server last answered. Never another workspace's: cleared on a change. */
  protected readonly longForm = signal<LongFormRead>({ state: 'loading' });

  /** The owner whose drafts are in hand or being read, so the same one is not asked for twice. */
  private longFormKey: string | null = null;

  readonly workspaceSlug = signal('');
  /** The kept run for this owner, or null. Never another owner's: it is cleared before the next is read. */
  private readonly kept = signal<ContentPipelineDraft | null>(null);
  /** The creative context that run is on, or null for one this device holds that is not on a context yet. */
  private readonly keptContextId = signal<string | null>(null);
  protected readonly discarded = signal(false);

  /** The owner key whose draft is in hand, so the same one is not read twice. */
  private readonly loadedKey = signal<string | null>(null);

  private readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  private readonly ownerKey = computed(() => {
    const membership = this.membership();

    return membership === null ? null : `${membership.workspaceId}.${membership.membershipId}`;
  });

  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';

    const membership = this.membership();
    if (membership === null) return 'not_found';
    if (membership.role === 'Viewer') return 'read_only';

    // Until this owner's draft has been read, "Start" would be a guess — and wrong for anyone mid-journey.
    return this.loadedKey() === this.ownerKey() ? 'ready' : 'loading';
  });

  protected readonly startLink = computed(() => this.linkTo(FIRST_CONTENT_PIPELINE_STEP));

  /** Always "Start": a Quick Recipe keeps its run in its own address, so there is nothing here to continue. */
  protected readonly quickRecipeLink = computed(() => ['/', this.workspaceSlug(), 'workflows', 'quick-recipe']);

  protected readonly longFormLink = computed(() => ['/', this.workspaceSlug(), 'workflows', 'long-form']);

  /**
   * The pieces to name: not finished, most recently changed first, and no more than a few.
   *
   * Accepted is finished and is left out. Accepted-and-since-stale is finished too, but it is the one kind of
   * finished piece that wants its creator, so it is named — as needing review, not as in progress.
   */
  protected readonly longFormRows = computed<readonly LongFormRow[]>(() => {
    const read = this.longForm();
    if (read.state !== 'read') return [];

    return read.pieces
      .filter((piece) => piece.status !== 'Accepted')
      .slice(0, WORKFLOWS_HUB_LONG_FORM_ROWS)
      .map((piece) => {
        const kind = LONG_FORM_KINDS[piece.kind];
        const needsReview = piece.status === 'NeedsReview';
        const step = LONG_FORM_PIPELINE_STEPS[longFormPipelineStepIndex(longFormStepFor(piece.status))]!.label;
        const at = new Date(piece.updatedAt);

        return {
          id: piece.id,
          kind,
          needsReview,
          step,
          when: Number.isNaN(at.getTime()) ? '' : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(at),

          // The piece, and no step: the pipeline opens it where it stands on the server now, which may be
          // further along than this list knew.
          link: [...this.longFormLink(), 'context', piece.creativeContextId, 'draft', piece.id],
          action: needsReview ? 'Open' : 'Continue',
          actionLabel: needsReview ? `Open the ${kind.toLowerCase()} that needs your review` : `Continue the ${kind.toLowerCase()} on ${step}`,
        };
      });
  });

  /** Where the creator got to, or null when there is nothing to go back to. */
  protected readonly resume = computed(() => {
    const draft = this.kept();
    // A draft that is all defaults is not a journey in progress, which is the pipeline's own reading too. A
    // run on a context is one however little of it is on this device: the rest is on the server.
    if (draft === null || (this.keptContextId() === null && isContentPipelineDraftEmpty(draft))) return null;

    const index = Math.max(0, contentPipelineStepIndex(draft.furthestStep));
    const step = CONTENT_PIPELINE_STEPS[index];
    const at = draft.savedAt === '' ? null : new Date(draft.savedAt);

    return {
      label: step.label,
      number: index + 1,
      link: this.linkTo(step.slug),
      when:
        at === null || Number.isNaN(at.getTime())
          ? ''
          : `on ${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(at)}`,
    };
  });

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          for (const params of maps) {
            const value = params.get('workspaceSlug');
            if (value) return value;
          }
          return null;
        }),
        takeUntilDestroyed(),
      )
      .subscribe((slug) => {
        if (slug === null) throw new Error('WorkflowsHubComponent route is missing a workspaceSlug segment.');
        if (slug === this.workspaceSlug()) return;

        // Nothing of the previous workspace may still be showing while the next one's draft is being read.
        this.workspaceSlug.set(slug);
        this.kept.set(null);
        this.keptContextId.set(null);
        this.discarded.set(false);
        this.loadedKey.set(null);
        this.longForm.set({ state: 'loading' });
        this.longFormKey = null;
      });

    // The long-form pieces are the server's to say, once the memberships say who is asking. A Viewer cannot
    // run the journey, so nothing is asked for one.
    effect(() => {
      const membership = this.membership();
      const key = this.ownerKey();
      if (membership === null || key === null || membership.role === 'Viewer' || key === this.longFormKey) return;

      this.longFormKey = key;
      void this.readLongForm(this.workspaceSlug(), key);
    });

    // Read once the memberships say who is asking — which is also what re-reads on a change of workspace, or of
    // who is signed in on this browser. A Viewer cannot run the journey, so nothing is read for one.
    effect(() => {
      const membership = this.membership();
      const key = this.ownerKey();
      if (membership === null || key === null || key === this.loadedKey()) return;

      const owner: ContentPipelineDraftOwner = {
        workspaceId: membership.workspaceId,
        membershipId: membership.membershipId,
      };
      const read = membership.role === 'Viewer' ? { draft: null, discarded: false } : this.drafts.read(owner);
      // A whole draft not yet on a context comes first: opening the pipeline files it, and it is the creator's
      // most recent unsaved work. Otherwise, the run this device last had open on a context (AF.3.1).
      const unfiled = read.draft !== null && !isContentPipelineDraftEmpty(read.draft) ? read.draft : null;
      const last = membership.role === 'Viewer' || unfiled !== null ? null : this.drafts.lastContextId(owner);
      const kept = last === null ? null : this.drafts.readKept(owner, last);

      this.kept.set(unfiled ?? kept?.kept?.draft ?? null);
      this.keptContextId.set(kept?.kept ? last : null);
      this.discarded.set(read.discarded || (kept?.discarded ?? false));
      this.loadedKey.set(key);
    });
  }

  /** Every route node from this one up to the root, so the slug can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }

  private linkTo(step: string): readonly string[] {
    const contextId = this.keptContextId();
    const base = ['/', this.workspaceSlug()];

    return contextId === null
      ? [...base, 'workflows', 'content-pipeline', step]
      : [...base, ...HANDOFF_ROUTES.contentPipeline(contextId), step];
  }

  protected retry(): void {
    void this.memberships.load();
  }

  protected retryLongForm(): void {
    const key = this.ownerKey();

    if (key !== null) void this.readLongForm(this.workspaceSlug(), key);
  }

  private async readLongForm(slug: string, key: string): Promise<void> {
    this.longForm.set({ state: 'loading' });

    const outcome = await this.longFormDrafts.list(slug, { limit: LONG_FORM_LOOKBACK });

    // Another workspace, or another person, arrived while this was being read: the answer is not theirs.
    if (key !== this.longFormKey || slug !== this.workspaceSlug()) return;

    this.longForm.set(
      outcome.status === 'found'
        ? { state: 'read', pieces: outcome.page.items }
        // A list this member may not read is a list with nothing in it for them, not a fault to report.
        : outcome.status === 'forbidden'
          ? { state: 'read', pieces: [] }
          : { state: 'unavailable' },
    );
  }
}
