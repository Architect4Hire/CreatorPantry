import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  WritableSignal,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpCardComponent,
  CpCardGridComponent,
  CpEmptyStateComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpSearchFieldComponent,
  CpStatusPillComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';
import { combineLatest, map } from 'rxjs';

import {
  CONTENT_PIPELINE_STEPS,
  CONTENT_PIPELINE_STEP_COUNT,
  ContentPipelineDraft,
  FIRST_CONTENT_PIPELINE_STEP,
  contentPipelineStepIndex,
  isContentPipelineDraftEmpty,
} from '../../models/content-pipeline.models';
import { LongFormDraftStatus, LongFormDraftSummary, LongFormKind } from '../../models/long-form-draft.models';
import {
  LONG_FORM_PIPELINE_STEPS,
  LongFormPipelineStepSlug,
  longFormPipelineStepIndex,
} from '../../models/long-form-pipeline.models';
import {
  WorkflowDefinitionSummary,
  WorkflowRunSummary,
  WorkflowRunSummaryPage,
  WorkspaceRoleName,
  attentionReasonText,
} from '../../models/workflow-hub.models';
import { ContentPipelineDraftOwner, ContentPipelineDraftService } from '../../services/content-pipeline-draft.service';
import { LongFormDraftService } from '../../services/long-form-draft.service';
import { WorkflowHubService } from '../../services/workflow-hub.service';
import { WorkflowRunService } from '../../services/workflow-run.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';

/** How many rows each list on the hub shows before "Show more". */
export const WORKFLOWS_HUB_ROWS = 5;

/** How far back the long-form read looks: the workspace's most recently changed pieces. */
const LONG_FORM_LOOKBACK = 20;

/** The catalogue's blog-and-newsletter workflow, which opens the long-form writer as built (LF.6.3). */
export const LONG_FORM_DEFINITION_KEY = 'content.blog-and-seo';

const LONG_FORM_KINDS: Readonly<Record<LongFormKind, string>> = {
  BlogArticle: 'Blog article',
  NewsletterIssue: 'Newsletter issue',
};

/** Where in the workspace each prerequisite's page is, said as the link that goes there. */
const NEED_LINKS: Readonly<Record<string, string>> = {
  brand: 'Open brand settings',
  recipes: 'Open your recipes',
  dam: 'Open your pictures',
  'content-board': 'Open your board',
};

/** A read the hub makes, as it stands: not answered yet, answered, or not answerable right now. */
type Read<T> =
  | { readonly state: 'loading' }
  | { readonly state: 'read'; readonly value: T }
  | { readonly state: 'unavailable' };

type Phase = 'loading' | 'unavailable' | 'not_found' | 'ready';

/** The three lists of runs the hub reads, each its own page and its own "Show more". */
export type HubRunView = 'mine' | 'attention' | 'completed';

/** A part of the page that can be read again on its own. */
export type HubSection = 'continue' | 'attention' | 'outcomes' | 'definitions';

export interface HubRow {
  readonly id: string;
  /** What the work is for, in the workflow's own words. */
  readonly goal: string;
  /** The creator's own name for it, when it is not just the goal again. */
  readonly title: string | null;
  readonly detail: string;
  readonly lines: readonly string[];
  readonly when: string;
  readonly at: number;
  readonly link: readonly string[];
  readonly action: string;
  readonly actionLabel: string;
}

interface GoalLink {
  readonly label: string;
  readonly ariaLabel: string;
  readonly link: readonly string[];
  readonly queryParams?: Readonly<Record<string, string>>;
  readonly primary: boolean;
}

interface GoalNeed {
  readonly message: string;
  readonly link: readonly string[] | null;
  readonly linkText: string;
}

export interface GoalCard {
  readonly id: string;
  readonly goal: string;
  readonly description: string;
  readonly outcome: string;
  /** A catalogue workflow is started as a run; a journey with a page of its own is opened. */
  readonly start: { readonly kind: 'run'; readonly key: string } | { readonly kind: 'links'; readonly links: readonly GoalLink[] };
  readonly canStart: boolean;
  readonly roleNote: string | null;
  readonly needs: readonly GoalNeed[];
}

/** The step a long-form piece is at, for telling a creator where they left it. */
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

function time(iso: string): number {
  const at = new Date(iso).getTime();
  return Number.isNaN(at) ? 0 : at;
}

function whenText(iso: string, style: 'day' | 'dayTime' = 'dayTime'): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return '';
  return new Intl.DateTimeFormat(undefined, style === 'day' ? { dateStyle: 'medium' } : { dateStyle: 'medium', timeStyle: 'short' }).format(at);
}

/** Who may start something that needs `role`, in words. */
function roleNote(role: WorkspaceRoleName): string {
  switch (role) {
    case 'Owner':
      return 'Only an Owner of this workspace can start this.';
    case 'Editor':
      return 'Only an Editor or Owner of this workspace can start this.';
    default:
      return 'Only a Contributor, Editor or Owner of this workspace can start this. You have view-only access here.';
  }
}

function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

/**
 * The Workflow Hub (13A.13): what the creator was doing, what needs a look, what they can set out to do, and what
 * they finished. A detail page about the workspace's work, in the dashboard's pattern — topic cards that each stand or
 * fail on their own, and a grid of goals.
 *
 * <p><strong>Continue where you left off</strong> is the creator's own work in progress, wherever it is kept: their
 * guided runs (the server's), the workspace's long-form pieces (the server's), and the Content Pipeline run this
 * device keeps, by workspace and membership. Newest first, whichever it is.</p>
 *
 * <p><strong>What do you want to accomplish?</strong> is the workspace's workflows as the server offers them —
 * started as a run in the shared shell, except "Write a blog post or newsletter", which opens the long-form writer as
 * built (LF.6.3) — and the two journeys with pages of their own. A workflow not built yet is not shown. One that this
 * reader cannot start says who can, or what it needs first and where to sort it out: a card never offers a Start that
 * a start would refuse.</p>
 *
 * <p>Each section reads its own data and says so when it cannot; only not knowing the creator's memberships stops the
 * page. An unknown workspace and one this creator is not in answer alike.</p>
 */
@Component({
  selector: 'cp-workflows-hub',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpCardGridComponent,
    CpEmptyStateComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpSearchFieldComponent,
    CpStatusPillComponent,
    CpTopicCardComponent,
  ],
  templateUrl: './workflows-hub.component.html',
  styleUrl: './workflows-hub.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkflowsHubComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly hub = inject(WorkflowHubService);
  private readonly runs = inject(WorkflowRunService);
  private readonly drafts = inject(ContentPipelineDraftService);
  private readonly longFormDrafts = inject(LongFormDraftService);
  private readonly injector = inject(Injector);

  private readonly continueCard = viewChild<CpTopicCardComponent>('continueCard');
  private readonly attentionCard = viewChild<CpTopicCardComponent>('attentionCard');
  private readonly outcomesCard = viewChild<CpTopicCardComponent>('outcomesCard');
  private readonly goalsHeading = viewChild<ElementRef<HTMLElement>>('goalsHeading');

  readonly workspaceSlug = signal('');

  protected readonly definitions = signal<Read<readonly WorkflowDefinitionSummary[]>>({ state: 'loading' });
  protected readonly mine = signal<Read<WorkflowRunSummaryPage>>({ state: 'loading' });
  protected readonly attention = signal<Read<WorkflowRunSummaryPage>>({ state: 'loading' });
  protected readonly finished = signal<Read<WorkflowRunSummaryPage>>({ state: 'loading' });
  protected readonly longForm = signal<Read<readonly LongFormDraftSummary[]>>({ state: 'loading' });

  /** The Content Pipeline run this device keeps for this owner, and the context it is on. */
  private readonly kept = signal<{ readonly draft: ContentPipelineDraft; readonly contextId: string | null } | null>(null);
  protected readonly discarded = signal(false);

  protected readonly query = signal('');
  /** How many rows each list shows; "Show more" adds five. */
  protected readonly shown: Readonly<Record<HubRunView, WritableSignal<number>>> = {
    mine: signal(WORKFLOWS_HUB_ROWS),
    attention: signal(WORKFLOWS_HUB_ROWS),
    completed: signal(WORKFLOWS_HUB_ROWS),
  };
  protected readonly loadingMore = signal<HubRunView | null>(null);
  /** The list whose next page could not be read, said beside its Show more. */
  protected readonly moreProblem = signal<HubRunView | null>(null);
  /** Sections being read again: their notice stays, saying so, until the answer comes. */
  protected readonly retrying = signal<ReadonlySet<HubSection>>(new Set());
  protected readonly starting = signal<string | null>(null);
  protected readonly startProblems = signal<Readonly<Record<string, string>>>({});

  /** The owner whose reads are in hand, so the same one is not asked for twice and a late answer is not believed. */
  private readKey: string | null = null;
  private readonly startKeys = new Map<string, string>();

  private readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;
    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';
    return this.membership() === null ? 'not_found' : 'ready';
  });

  readonly viewOnly = computed(() => this.membership()?.role === 'Viewer');

  // ---- Continue where you left off ----

  readonly continueRows = computed<readonly HubRow[]>(() => {
    const rows: HubRow[] = [];
    const slug = this.workspaceSlug();

    const mine = this.mine();
    if (mine.state === 'read') {
      for (const run of mine.value.items) {
        const done = run.completedStepCount + run.skippedStepCount;
        rows.push({
          id: `run:${run.id}`,
          goal: run.goal,
          title: run.title !== run.goal ? run.title : null,
          detail: run.nextStepTitle
            ? `Next: ${run.nextStepTitle} · ${done} of ${run.stepCount} steps done`
            : 'Every step is done. Finish when you’re ready.',
          lines: [],
          when: whenText(run.updatedAt),
          at: time(run.updatedAt),
          link: ['/', slug, 'workflows', 'runs', run.id],
          action: 'Continue',
          actionLabel: `Continue “${run.title}”`,
        });
      }
    }

    const longForm = this.longForm();
    if (longForm.state === 'read') {
      for (const piece of longForm.value.filter((p) => p.status !== 'Accepted' && p.status !== 'NeedsReview')) {
        const kind = LONG_FORM_KINDS[piece.kind];
        const step = LONG_FORM_PIPELINE_STEPS[longFormPipelineStepIndex(longFormStepFor(piece.status))]!.label;
        rows.push({
          id: `long-form:${piece.id}`,
          goal: 'Write a blog post or newsletter',
          title: kind,
          detail: `On ${step}`,
          lines: [],
          when: whenText(piece.updatedAt),
          at: time(piece.updatedAt),
          link: this.longFormPieceLink(piece),
          action: 'Continue',
          actionLabel: `Continue the ${kind.toLowerCase()} on ${step}`,
        });
      }
    }

    const kept = this.kept();
    if (kept) {
      const index = Math.max(0, contentPipelineStepIndex(kept.draft.furthestStep));
      const step = CONTENT_PIPELINE_STEPS[index];
      rows.push({
        id: 'content-pipeline',
        goal: 'Make a picture and posts from one idea',
        title: null,
        detail: `${step.label} · step ${index + 1} of ${CONTENT_PIPELINE_STEP_COUNT} · kept on this device`,
        lines: [],
        when: kept.draft.savedAt ? whenText(kept.draft.savedAt) : '',
        at: kept.draft.savedAt ? time(kept.draft.savedAt) : 0,
        link: this.pipelineLink(step.slug),
        action: 'Continue',
        actionLabel: 'Continue making a picture and posts',
      });
    }

    return rows.sort((a, b) => b.at - a.at);
  });

  readonly continueVisible = computed(() => this.continueRows().slice(0, this.shown.mine()));

  readonly continueHasMore = computed(() => this.hasMore('mine', this.continueRows().length));

  /** Some of the creator's work in progress could not be read. */
  readonly continueDegraded = computed(() => this.mine().state === 'unavailable' || this.longForm().state === 'unavailable');

  // ---- Needs attention ----

  readonly attentionRows = computed<readonly HubRow[]>(() => {
    const rows: HubRow[] = [];
    const slug = this.workspaceSlug();
    const me = this.membership()?.membershipId ?? null;

    const attention = this.attention();
    if (attention.state === 'read') {
      for (const run of attention.value.items) {
        const theirs = run.owner.membershipId !== me
          ? `Started by ${run.owner.displayName ?? 'someone who has left the workspace'}.`
          : null;
        rows.push({
          id: `run:${run.id}`,
          goal: run.goal,
          title: run.title !== run.goal ? run.title : null,
          detail: theirs ?? '',
          lines: run.attentionReasons.map(attentionReasonText),
          when: whenText(run.updatedAt),
          at: time(run.updatedAt),
          link: ['/', slug, 'workflows', 'runs', run.id],
          action: 'Open',
          actionLabel: `Open “${run.title}”`,
        });
      }
    }

    const longForm = this.longForm();
    if (longForm.state === 'read') {
      for (const piece of longForm.value.filter((p) => p.status === 'NeedsReview')) {
        const kind = LONG_FORM_KINDS[piece.kind];
        rows.push({
          id: `long-form:${piece.id}`,
          goal: 'Write a blog post or newsletter',
          title: kind,
          detail: '',
          lines: ['Something it was written from has changed since you accepted it. It needs your review.'],
          when: whenText(piece.updatedAt),
          at: time(piece.updatedAt),
          link: this.longFormPieceLink(piece),
          action: 'Open',
          actionLabel: `Open the ${kind.toLowerCase()} that needs your review`,
        });
      }
    }

    return rows;
  });

  readonly attentionVisible = computed(() => this.attentionRows().slice(0, this.shown.attention()));

  readonly attentionHasMore = computed(() => this.hasMore('attention', this.attentionRows().length));

  readonly attentionDegraded = computed(() => this.attention().state === 'unavailable' || this.longForm().state === 'unavailable');

  // ---- Recent outcomes ----

  readonly outcomeRows = computed<readonly HubRow[]>(() => {
    const read = this.finished();
    if (read.state !== 'read') return [];
    const slug = this.workspaceSlug();
    return read.value.items.map((run) => ({
      id: `run:${run.id}`,
      goal: run.goal,
      title: run.title !== run.goal ? run.title : null,
      detail: '',
      lines: [],
      when: whenText(run.completedAt ?? run.updatedAt, 'day'),
      at: time(run.completedAt ?? run.updatedAt),
      link: ['/', slug, 'workflows', 'runs', run.id],
      action: 'See what you made',
      actionLabel: `See what you made with “${run.title}”`,
    }));
  });

  readonly outcomesVisible = computed(() => this.outcomeRows().slice(0, this.shown.completed()));

  readonly outcomesHasMore = computed(() => this.hasMore('completed', this.outcomeRows().length));

  // ---- What do you want to accomplish? ----

  readonly goalCards = computed<readonly GoalCard[]>(() => {
    const slug = this.workspaceSlug();
    const viewOnly = this.viewOnly();
    const cards: GoalCard[] = [];

    const read = this.definitions();
    if (read.state === 'read') {
      for (const definition of read.value.filter((d) => d.available)) {
        const needs = definition.waitingFor.map((need) => ({
          message: need.message,
          link: need.route === null ? null : ['/', slug, ...need.route.split('/')],
          linkText: (need.route && NEED_LINKS[need.route]) || 'Open',
        }));
        const base = {
          id: definition.key,
          goal: definition.goal,
          description: definition.description,
          outcome: definition.expectedOutcome,
          canStart: definition.canStart,
          roleNote: definition.neededRole ? roleNote(definition.neededRole) : null,
          needs,
        };

        cards.push(
          definition.key === LONG_FORM_DEFINITION_KEY
            ? {
                ...base,
                start: {
                  kind: 'links',
                  links: [
                    { label: 'Start from a recipe', ariaLabel: 'Start from a recipe: a blog post or newsletter', link: this.longFormLink(), queryParams: { from: 'recipe' }, primary: true },
                    { label: 'Start from a brief', ariaLabel: 'Start from a brief: a blog post or newsletter', link: this.longFormLink(), queryParams: { from: 'brief' }, primary: false },
                  ],
                },
              }
            : { ...base, start: { kind: 'run', key: definition.key } },
        );
      }
    }

    // The two journeys with pages of their own, and no catalogue entry: shown however the catalogue read went.
    const local = { canStart: !viewOnly, roleNote: viewOnly ? roleNote('Contributor') : null, needs: [] };
    cards.push(
      {
        id: 'content-pipeline',
        goal: 'Make a picture and posts from one idea',
        description: 'From an idea to a picture and the posts that go out with it, one step at a time.',
        outcome: 'A picture and a post for each of your channels, ready for your board.',
        start: { kind: 'links', links: [{ label: 'Start', ariaLabel: 'Start making a picture and posts', link: this.pipelineLink(FIRST_CONTENT_PIPELINE_STEP), primary: true }] },
        ...local,
      },
      {
        id: 'quick-recipe',
        goal: 'Draft a recipe from a dish name',
        description: 'Name a dish, and get a draft recipe and a lead picture for it, saved together.',
        outcome: 'A draft recipe with its picture in your library, ready to work on.',
        start: { kind: 'links', links: [{ label: 'Start', ariaLabel: 'Start drafting a recipe from a dish name', link: ['/', slug, 'workflows', 'quick-recipe'], primary: true }] },
        ...local,
      },
    );

    return cards;
  });

  readonly shownGoals = computed(() => {
    const words = this.query().trim().toLowerCase().split(/\s+/).filter((word) => word !== '');
    if (words.length === 0) return this.goalCards();
    return this.goalCards().filter((card) => {
      const text = `${card.goal} ${card.description} ${card.outcome}`.toLowerCase();
      return words.every((word) => text.includes(word));
    });
  });

  readonly matchText = computed(() => {
    if (this.query().trim() === '') return '';
    const shown = this.shownGoals().length;
    return shown === 0 ? 'Nothing matches that.' : `${shown} of ${this.goalCards().length} shown.`;
  });

  // ---- First use ----

  /** Everything is read, and there is nothing in progress, needing a look, or finished: a creator new to this. */
  readonly firstUse = computed(() => {
    const reads = [this.mine(), this.attention(), this.finished()];
    const longForm = this.longForm();
    if (reads.some((read) => read.state !== 'read')) return false;
    if (!this.viewOnly() && longForm.state !== 'read') return false;
    return this.continueRows().length === 0 && this.attentionRows().length === 0 && this.outcomeRows().length === 0;
  });

  /** Still finding out what the creator has in progress. */
  readonly checking = computed(() => [this.mine(), this.attention(), this.finished()].some((read) => read.state === 'loading'));

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => maps.map((params) => params.get('workspaceSlug')).find((value) => !!value) ?? null),
        takeUntilDestroyed(),
      )
      .subscribe((slug) => {
        if (slug === null) throw new Error('WorkflowsHubComponent route is missing a workspaceSlug segment.');
        if (slug === this.workspaceSlug()) return;

        // Nothing of the previous workspace may still be showing while the next one is read.
        this.workspaceSlug.set(slug);
        this.reset();
      });

    // Read once the memberships say who is asking — which is also what reads again for another workspace, or for
    // someone else signed in on this browser.
    effect(() => {
      const membership = this.membership();
      if (membership === null) return;
      const key = `${membership.workspaceId}.${membership.membershipId}`;
      if (key === this.readKey) return;

      this.readKey = key;
      this.readAll(this.workspaceSlug(), key, membership.role === 'Viewer', {
        workspaceId: membership.workspaceId,
        membershipId: membership.membershipId,
      });
    });
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }

  /**
   * Reads one section again. Its notice stays — saying it is trying again — until the answer comes, so the button
   * pressed is never pulled out from under the keyboard; then focus goes to the section's heading.
   */
  protected async retrySection(section: HubSection): Promise<void> {
    if (this.retrying().has(section)) return;
    this.retrying.update((set) => new Set([...set, section]));

    const slug = this.workspaceSlug();
    const key = this.readKey;
    const longFormToo = this.longForm().state === 'unavailable' && !this.viewOnly();
    const reads: Promise<void>[] = [];
    switch (section) {
      case 'definitions':
        reads.push(this.readDefinitions(slug, key, { keep: true }));
        break;
      case 'continue':
        if (this.mine().state === 'unavailable') reads.push(this.readRuns(slug, key, 'mine', { keep: true }));
        if (longFormToo) reads.push(this.readLongForm(slug, key, { keep: true }));
        break;
      case 'attention':
        if (this.attention().state === 'unavailable') reads.push(this.readRuns(slug, key, 'attention', { keep: true }));
        if (longFormToo) reads.push(this.readLongForm(slug, key, { keep: true }));
        break;
      case 'outcomes':
        if (this.finished().state === 'unavailable') reads.push(this.readRuns(slug, key, 'completed', { keep: true }));
        break;
    }

    await Promise.all(reads);
    this.retrying.update((set) => new Set([...set].filter((item) => item !== section)));
    if (key === this.readKey) this.focusSection(section);
  }

  /** Shows five more rows of a list, reading its next page of runs when the rows in hand run out. */
  protected async showMore(view: HubRunView): Promise<void> {
    if (this.loadingMore() !== null) return;
    this.shown[view].update((shown) => shown + WORKFLOWS_HUB_ROWS);
    await this.fetchMore(view);
  }

  /** Tries the next page of a list again, after it could not be read. */
  protected retryMore(view: HubRunView): Promise<void> {
    return this.loadingMore() !== null ? Promise.resolve() : this.fetchMore(view);
  }

  private async fetchMore(view: HubRunView): Promise<void> {
    const into = this.pageOf(view);
    const page = into();
    const rows = view === 'mine' ? this.continueRows() : view === 'attention' ? this.attentionRows() : this.outcomeRows();
    if (page.state !== 'read' || page.value.nextCursor === null || rows.length >= this.shown[view]()) return;

    const slug = this.workspaceSlug();
    const key = this.readKey;
    this.loadingMore.set(view);
    this.moreProblem.set(null);
    const outcome = await this.hub.runs(slug, { view, limit: WORKFLOWS_HUB_ROWS * 2, cursor: page.value.nextCursor });
    this.loadingMore.set(null);
    if (key !== this.readKey) return;

    if (outcome.status !== 'found') {
      this.moreProblem.set(view);
      return;
    }

    into.set({ state: 'read', value: { items: [...page.value.items, ...outcome.page.items], nextCursor: outcome.page.nextCursor } });
  }

  private hasMore(view: HubRunView, rows: number): boolean {
    const page = this.pageOf(view)();
    return rows > this.shown[view]() || (page.state === 'read' && page.value.nextCursor !== null);
  }

  private pageOf(view: HubRunView): WritableSignal<Read<WorkflowRunSummaryPage>> {
    return view === 'mine' ? this.mine : view === 'attention' ? this.attention : this.finished;
  }

  private focusSection(section: HubSection): void {
    afterNextRender(
      () => {
        const card = section === 'continue' ? this.continueCard() : section === 'attention' ? this.attentionCard() : section === 'outcomes' ? this.outcomesCard() : undefined;
        if (card) card.focusHeading();
        else this.goalsHeading()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  protected setQuery(value: string): void {
    this.query.set(value);
  }

  protected onSearchInput(event: Event): void {
    const target = event.target;
    this.query.set(target instanceof HTMLInputElement ? target.value : '');
  }

  /** Starts a catalogue workflow as a run, and opens it in the guided shell. */
  protected async start(card: GoalCard): Promise<void> {
    if (card.start.kind !== 'run' || this.starting() !== null || !card.canStart) return;

    const definitionKey = card.start.key;
    const slug = this.workspaceSlug();
    // One key per attempt, kept across a retry of it, so a lost answer never starts two runs.
    const idempotencyKey = this.startKeys.get(card.id) ?? newKey();
    this.startKeys.set(card.id, idempotencyKey);
    this.starting.set(card.id);
    this.problemFor(card.id, null);

    const outcome = await this.runs.start(slug, definitionKey, idempotencyKey);
    this.starting.set(null);

    switch (outcome.status) {
      case 'started':
        this.startKeys.delete(card.id);
        await this.router.navigate(['/', slug, 'workflows', 'runs', outcome.run.id]);
        return;
      case 'forbidden':
        this.startKeys.delete(card.id);
        this.problemFor(card.id, 'Your access in this workspace doesn’t let you start this.');
        // Read the cards again, in place, so this one names who can.
        void this.readDefinitions(slug, this.readKey, { keep: true });
        return;
      case 'unprocessable':
        this.startKeys.delete(card.id);
        this.problemFor(card.id, outcome.message);
        // What it needs may have changed: read the cards again, in place, so each says what is true now.
        void this.readDefinitions(slug, this.readKey, { keep: true });
        return;
      case 'unavailable':
        this.problemFor(card.id, outcome.offline
          ? 'You’re offline. Nothing was started — try again when you’re back online.'
          : 'This couldn’t be started just now. Nothing was started — try again.');
        return;
      default:
        this.startKeys.delete(card.id);
        this.problemFor(card.id, 'This couldn’t be started just now. Try again.');
    }
  }

  // ---- Reads ----

  private reset(): void {
    this.readKey = null;
    this.definitions.set({ state: 'loading' });
    this.mine.set({ state: 'loading' });
    this.attention.set({ state: 'loading' });
    this.finished.set({ state: 'loading' });
    this.longForm.set({ state: 'loading' });
    this.kept.set(null);
    this.discarded.set(false);
    this.query.set('');
    for (const view of ['mine', 'attention', 'completed'] as const) this.shown[view].set(WORKFLOWS_HUB_ROWS);
    this.loadingMore.set(null);
    this.moreProblem.set(null);
    this.retrying.set(new Set());
    this.startProblems.set({});
    this.startKeys.clear();
  }

  private readAll(slug: string, key: string, viewOnly: boolean, owner: ContentPipelineDraftOwner): void {
    void this.readDefinitions(slug, key);
    void this.readRuns(slug, key, 'mine');
    void this.readRuns(slug, key, 'attention');
    void this.readRuns(slug, key, 'completed');

    if (viewOnly) {
      // A Viewer makes nothing, so has nothing of their own in progress here.
      this.longForm.set({ state: 'read', value: [] });
      this.kept.set(null);
      return;
    }

    void this.readLongForm(slug, key);
    this.readKept(owner);
  }

  /** `keep` leaves what is shown in place until the answer comes, for a read again rather than a first read. */
  private async readDefinitions(slug: string, key: string | null, options: { readonly keep?: boolean } = {}): Promise<void> {
    if (!options.keep) this.definitions.set({ state: 'loading' });
    const outcome = await this.hub.definitions(slug);
    if (key !== this.readKey) return;
    this.definitions.set(outcome.status === 'found' ? { state: 'read', value: outcome.definitions } : { state: 'unavailable' });
  }

  private async readRuns(slug: string, key: string | null, view: HubRunView, options: { readonly keep?: boolean } = {}): Promise<void> {
    const into = this.pageOf(view);
    if (!options.keep) into.set({ state: 'loading' });
    const outcome = await this.hub.runs(slug, { view, limit: WORKFLOWS_HUB_ROWS * 2 });
    if (key !== this.readKey) return;
    into.set(outcome.status === 'found' ? { state: 'read', value: outcome.page } : { state: 'unavailable' });
  }

  private async readLongForm(slug: string, key: string | null, options: { readonly keep?: boolean } = {}): Promise<void> {
    if (!options.keep) this.longForm.set({ state: 'loading' });
    const outcome = await this.longFormDrafts.list(slug, { limit: LONG_FORM_LOOKBACK });
    if (key !== this.readKey) return;
    this.longForm.set(
      outcome.status === 'found'
        ? { state: 'read', value: outcome.page.items }
        // A list this member may not read is a list with nothing in it for them, not a fault to report.
        : outcome.status === 'forbidden'
          ? { state: 'read', value: [] }
          : { state: 'unavailable' },
    );
  }

  /**
   * The Content Pipeline run this device keeps for this owner: a whole draft not yet on a context first — the
   * pipeline files it when opened — otherwise the run it last had open on a context. Reading a draft that cannot be
   * read throws it away, which is the draft service's behaviour; this page is where the creator is told.
   */
  private readKept(owner: ContentPipelineDraftOwner): void {
    const read = this.drafts.read(owner);
    const unfiled = read.draft !== null && !isContentPipelineDraftEmpty(read.draft) ? read.draft : null;
    const last = unfiled !== null ? null : this.drafts.lastContextId(owner);
    const kept = last === null ? null : this.drafts.readKept(owner, last);

    this.kept.set(
      unfiled !== null
        ? { draft: unfiled, contextId: null }
        : kept?.kept
          ? { draft: kept.kept.draft, contextId: last }
          : null,
    );
    this.discarded.set(read.discarded || (kept?.discarded ?? false));
  }

  // ---- Links ----

  private longFormLink(): readonly string[] {
    return ['/', this.workspaceSlug(), 'workflows', 'long-form'];
  }

  /** The piece, and no step: the pipeline opens it where it stands on the server now. */
  private longFormPieceLink(piece: LongFormDraftSummary): readonly string[] {
    return [...this.longFormLink(), 'context', piece.creativeContextId, 'draft', piece.id];
  }

  private pipelineLink(step: string): readonly string[] {
    const contextId = this.kept()?.contextId ?? null;
    const base = ['/', this.workspaceSlug()];
    return contextId === null
      ? [...base, 'workflows', 'content-pipeline', step]
      : [...base, ...HANDOFF_ROUTES.contentPipeline(contextId), step];
  }

  private problemFor(cardId: string, message: string | null): void {
    this.startProblems.update((problems) => {
      const next = { ...problems };
      if (message === null) delete next[cardId];
      else next[cardId] = message;
      return next;
    });
  }

  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);
    return nodes;
  }
}
