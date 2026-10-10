import { Location, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { CONTEXT_ROUTE_PARAM, CONTEXT_ROUTE_SEGMENT } from '../../shared/use-this-in/handoff-destinations';
import { combineLatest, map } from 'rxjs';
import { CpButtonComponent, CpCardComponent, CpNoticeComponent, CpProgressComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_PIPELINE_STEPS,
  CONTENT_PIPELINE_STEP_COUNT,
  CONTENT_PIPELINE_STEP_SLUGS,
  ContentPipelineConfig,
  ContentPipelineDraft,
  ContentPipelineStepSlug,
  FIRST_CONTENT_PIPELINE_STEP,
  contentPipelineBriefAfter,
  contentPipelineConfigWith,
  STEP_STATUS_GLYPH,
  STEP_STATUS_WORD,
  contentPipelineStepIndex,
  contentPipelineStepStatus,
  emptyContentPipelineDraft,
  isContentPipelineDraftEmpty,
  isContentPipelineStepSlug,
} from '../../models/content-pipeline.models';
import {
  ContentPipelineDraftOwner,
  ContentPipelineDraftService,
} from '../../services/content-pipeline-draft.service';
import { CreativeContextFields } from '../../models/creative-context-fields.models';
import { keeperReferencesOf, linkedRecipeOf } from '../../models/creative-context.models';
import { DamKeepRecipe } from '../../models/dam-asset.models';
import { RecipePickerComponent } from '../../shared/recipe-picker/recipe-picker.component';
import { CreativeContextSession } from '../../services/creative-context-session';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { CreativeContextSaveNoticeComponent } from '../../shared/creative-context-save-notice/creative-context-save-notice.component';
import { ContentPipelineIdeaStepComponent } from './content-pipeline-idea-step.component';
import { ContentPipelineImagesStepComponent } from './content-pipeline-images-step.component';
import { ContentPipelineLibraryStepComponent } from './content-pipeline-library-step.component';
import { ContentPipelinePostsStepComponent } from './content-pipeline-posts-step.component';
import { ContentPipelinePromptStepComponent } from './content-pipeline-prompt-step.component';
import { ContentPipelineSetupStepComponent } from './content-pipeline-setup-step.component';
import { ContentPipelineStepPlaceholderComponent } from './content-pipeline-step-placeholder.component';

/**
 * `loading` and `unavailable` are how reading the creator's memberships can end; `not_found` and `read_only`
 * are the two refusals; `resumable` offers a kept run; `editing` is the journey itself.
 *
 * Three are about the run's creative context (AF.3.1). `context_not_found` and `context_unavailable` are how
 * reading the one the address names can end — and neither opens as an empty run. `filing_failed` is work found
 * on this device that could not be put on a context: it stays where it is until that works.
 */
type Phase =
  | 'loading'
  | 'unavailable'
  | 'not_found'
  | 'read_only'
  | 'context_not_found'
  | 'context_unavailable'
  | 'filing_failed'
  | 'resumable'
  | 'editing';

/**
 * What an unfiled run says its creative context should hold.
 *
 * The theme is the picked idea's, and only when that idea is for the day the run is for — a theme belongs to a
 * day, so one from another day would describe a week this run is not in.
 */
export function contentPipelineContextFields(draft: ContentPipelineDraft): CreativeContextFields {
  const accepted = draft.seed.accepted;

  return {
    workingTitle: draft.config.subject,
    channelKey: draft.config.channelKey,
    day: draft.config.day,
    pictureBrief: draft.config.concept,
    weeklyThemeKey: accepted !== null && accepted.day.day === draft.config.day ? (accepted.day.theme?.key ?? null) : null,
    briefSource: draft.config.briefSource,
    workingBrief: draft.config.brief,
  };
}

const HEADING_ID = 'cp-pipeline-step-heading';

/**
 * The Content Pipeline: the guided journey from an idea to finished posts (PIPE-UI-001/002).
 *
 * **It owns navigation and the kept draft; the steps own their fields.** A step is handed the draft and hands
 * back the next one, so there is exactly one copy of the creator's answers and what is on screen is always what
 * is kept.
 *
 * **What the run is about lives on its creative context** (AF.3.1): the channel, the day, that day's theme and
 * the picture the creator has in mind are read from the server and saved back as they change, through
 * `CreativeContextSession`. So a run opened on another device shows the same answers. What stays on this device
 * (`ContentPipelineDraftService`, keyed by the context's id) is the rest — the step reached, the idea picked,
 * request ids, and the creator's scene lines and prompt, which the context has no field for yet.
 *
 * **A run gets its context on the first answer, not on arrival**, so looking at the pipeline does not leave an
 * empty piece of work behind. A whole draft found on this device from before contexts existed is filed on one
 * before anything is shown, and is not touched until that has worked.
 *
 * **The step list is the whole journey, including the four steps not built yet**, so "Step 2 of 6" is the truth
 * about the pipeline rather than about how much of it has shipped. A step that is not built offers no action.
 *
 * **Leaving costs nothing, so leaving is not confirmed.** Every change is kept as it is made — on this device
 * at once, and an answer still on its way to the server is sent as the page goes — which is why there is no
 * unsaved-work guard here. Starting over does ask, because that is the one action that throws the run away.
 *
 * **The workspace comes from the route and the creator's own membership.** An unknown workspace and one they
 * cannot reach answer alike, so neither discloses that the other exists (.claude/rules/tenancy.md).
 */
@Component({
  selector: 'cp-content-pipeline-shell',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpNoticeComponent,
    CpProgressComponent,
    CreativeContextSaveNoticeComponent,
    RecipePickerComponent,
    ContentPipelineSetupStepComponent,
    ContentPipelineIdeaStepComponent,
    ContentPipelinePromptStepComponent,
    ContentPipelineImagesStepComponent,
    ContentPipelineLibraryStepComponent,
    ContentPipelinePostsStepComponent,
    ContentPipelineStepPlaceholderComponent,
  ],
  templateUrl: './content-pipeline-shell.component.html',
  styleUrl: './content-pipeline-shell.component.css',
  providers: [CreativeContextSession],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineShellComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  protected readonly session = inject(CreativeContextSession);
  private readonly drafts = inject(ContentPipelineDraftService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirm = inject(ConfirmService);
  private readonly injector = inject(Injector);

  readonly steps = CONTENT_PIPELINE_STEPS;
  readonly total = CONTENT_PIPELINE_STEP_COUNT;

  readonly workspaceSlug = signal('');
  readonly draft = signal<ContentPipelineDraft>(emptyContentPipelineDraft());
  /** Null until the step in the URL has been checked against how far the creator has actually got. */
  readonly currentIndex = signal<number | null>(null);
  readonly announcement = signal('');
  /** True when something was stored here that could not be read, so the creator is told rather than puzzled. */
  readonly discarded = signal(false);

  /** False until this creator's run has been read — its context from the server, the rest from this device. */
  private readonly loaded = signal(false);
  /** True while a kept run is being offered, before the creator has said Continue or Start over. */
  private readonly resumeOffered = signal(false);
  /** True when work found on this device could not be put on a context. It is still on the device. */
  private readonly filingFailed = signal(false);

  private stepParam: string | null = null;

  /** The creative context the address names, or null for the pipeline's bare address. */
  private readonly routeContextId = signal<string | null>(null);
  /** The context a kept run on this device belongs to, while that run is being offered for resuming. */
  private resumeContextId: string | null = null;
  /** What is currently loaded — an owner and the context the address names — so it is not read twice. */
  private loadedKey: string | null = null;
  /** That owner. Every write goes under it, never under whoever the route names by the time it lands. */
  private loadedOwner: ContentPipelineDraftOwner | null = null;
  /** Raised by each open, so an answer to an earlier one lands nowhere. */
  private openTicket = 0;
  /** True once something has been kept for this context here, so merely opening one does not leave a record. */
  private keptHere = false;
  /** The context this run was filed on since it was opened, so that is recorded once. */
  private filedContextId: string | null = null;

  /**
   * The context this run is on: the one in the address, or the one it was filed on since.
   *
   * The second matters because a run that gets its context mid-typing keeps the same screen — only the address
   * changes — so the route's own parameter still says there is none.
   */
  private get contextId(): string | null {
    return this.session.context()?.id ?? this.resumeContextId ?? this.routeContextId();
  }

  readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  /**
   * Whose pipeline this is: the workspace by id, and the membership reading it.
   *
   * Null until the creator's memberships have been read, which is why the draft cannot be loaded from the route
   * alone — the slug names a workspace, and a kept draft belongs to one *person* in one workspace.
   */
  readonly owner = computed<ContentPipelineDraftOwner | null>(() => {
    const membership = this.membership();

    return membership === null
      ? null
      : { workspaceId: membership.workspaceId, membershipId: membership.membershipId };
  });

  /**
   * One string for that owner, used to key the step on screen.
   *
   * A step is destroyed and built again when this changes, which is what keeps one workspace's idea — or one
   * colleague's — from staying on screen under another's name. Without it the shell swaps the draft underneath
   * child components that are still holding the previous one's state (.claude/rules/tenancy.md).
   */
  readonly ownerKey = computed(() => {
    const owner = this.owner();

    return owner === null ? '' : `${owner.workspaceId}.${owner.membershipId}`;
  });

  readonly currentStep = computed(() => {
    const index = this.currentIndex();
    return index === null ? null : CONTENT_PIPELINE_STEPS[index];
  });

  /** A list of one, so the template can name the step with `@for` and never index into it. */
  readonly currentStepList = computed(() => {
    const step = this.currentStep();
    return step === null ? [] : [step];
  });

  readonly currentSlug = computed<ContentPipelineStepSlug | null>(() => {
    const index = this.currentIndex();
    return index === null ? null : CONTENT_PIPELINE_STEP_SLUGS[index];
  });

  readonly rows = computed(() => {
    const current = this.currentSlug();
    const furthest = this.draft().furthestStep;

    return CONTENT_PIPELINE_STEPS.map((step) => {
      const status = contentPipelineStepStatus(step.slug, current, furthest);
      return { step, status, glyph: STEP_STATUS_GLYPH[status], word: STEP_STATUS_WORD[status] };
    });
  });

  readonly stepNumber = computed(() => (this.currentIndex() ?? 0) + 1);
  readonly progressText = computed(() => `Step ${this.stepNumber()} of ${this.total}`);
  readonly percent = computed(() => Math.round((this.stepNumber() / this.total) * 100));
  readonly isFirst = computed(() => this.currentIndex() === 0);

  /** True for a step whose body is not built. It is passed through rather than stopped at — see `canContinue`. */
  readonly isComingSoon = computed(() => this.currentStep()?.comingSoon !== null);

  /** True on the last step, which has nowhere to continue to. */
  readonly isLast = computed(() => this.currentIndex() === this.total - 1);

  /**
   * Whether Continue is offered.
   *
   * Setup asks nothing required, so it always continues. The idea step continues once an idea has been picked —
   * which is what picking one is for.
   *
   * **A step that is not built is passed through rather than blocking the journey** (AF.4.3). It asks nothing,
   * so there is nothing to answer, and the step after it may well be built — stopping there would hide a
   * finished step behind an unfinished one. Every step is built as of AF.6.5, and the rule stays: the next
   * one added will arrive unbuilt like the rest did.
   *
   * **The posts step asks nothing required either.** A creator may carry on to their library without writing
   * a post, and gating it would make a journey about pictures depend on words they did not want.
   */
  readonly canContinue = computed(() => {
    const slug = this.currentSlug();
    if (slug === null) return false;
    // An idea, and — where the creator also described a picture — their answer to which of the two to work
    // from. That is asked rather than assumed: it decides what every later step plans around.
    if (slug === 'idea') return this.draft().seed.accepted !== null && !this.needsBriefChoice();
    // A prompt is what this step is for, so carrying on means having one. The creator's own text counts: a
    // prompt they wrote themselves is as finished as one that was written for them.
    if (slug === 'prompt') return this.draft().prompt.finalPrompt.trim() !== '';
    // A picture is what the steps after this one are about, so one has to be kept. Pictures that came back and
    // were not chosen are not a decision, which is why the keepers rather than the run are what count. Read
    // from the work itself since AF.4.3, which is where a keeper now lives.
    if (slug === 'images') return this.keepers().length > 0;

    return contentPipelineStepIndex(slug) < this.total - 1;
  });

  /** The pictures this work names as keepers: what the images step marked, and what the library step files. */
  readonly keepers = computed(() => keeperReferencesOf(this.session.context()));

  /**
   * Where the creator's answers are, said plainly.
   *
   * Two places, and it says both: what the run is about is saved to the workspace, and the rest is on this
   * device. Anything that needs the creator's attention is the save notice's to say, not this line's.
   */
  /** The recipe this run is about, read from its context: what both picture requests name. */
  readonly linkedRecipe = computed(() => linkedRecipeOf(this.session.context()));

  /**
   * That recipe with its title, as the picker resolved it.
   *
   * The context names a recipe by id, and the setup step's hint about which answer the run is planned around
   * has to show *which* recipe. The picker reads it already, so it is taken from there rather than read again.
   */
  private readonly namedRecipe = signal<DamKeepRecipe | null>(null);

  protected readonly linkedRecipeTitle = computed(() => this.namedRecipe()?.title ?? null);

  /** True when there is a description and a picked idea, and the creator has not said which to work from. */
  readonly needsBriefChoice = computed(() => {
    const { config, seed } = this.draft();

    return seed.accepted !== null && config.concept.trim() !== '' && config.briefSource === null;
  });

  readonly savedText = computed(() => {
    const context = this.session.context();
    if (context === null) return 'Your answers are kept on this device as you go.';

    const save = this.session.save();
    if (save === 'pending' || save === 'saving') return 'Saving…';
    if (save !== 'saved') return 'Not saved yet.';

    const at = new Date(context.updatedAt);
    const rest = 'Your idea, prompt and pictures are kept on this device.';
    if (Number.isNaN(at.getTime())) return `Saved. ${rest}`;

    return `Saved at ${new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(at)}. ${rest}`;
  });

  /** When the run on offer was last touched here, for the resume card. */
  readonly keptText = computed(() => {
    const savedAt = this.draft().savedAt;
    const at = savedAt === '' ? null : new Date(savedAt);
    if (at === null || Number.isNaN(at.getTime())) return '';

    return `You last worked on it at ${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(at)}.`;
  });

  readonly resumeStep = computed(() => CONTENT_PIPELINE_STEPS[contentPipelineStepIndex(this.draft().furthestStep)]);


  /**
   * Derived rather than set, so the screen cannot be left claiming a phase the data has moved past.
   *
   * **An unknown workspace and one this creator is not in answer alike**, because telling the two apart would
   * disclose that the other exists (.claude/rules/tenancy.md).
   *
   * **A Viewer is stopped at the door rather than led through.** The idea generator itself is readable by every
   * member, but the steps this journey ends in are not, and walking someone to a wall is worse than saying so.
   */
  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';

    const membership = this.membership();
    if (membership === null) return 'not_found';
    if (membership.role === 'Viewer') return 'read_only';
    if (this.filingFailed()) return 'filing_failed';

    if (this.routeContextId() !== null) {
      // An id that does not resolve is said, never shown as a run with nothing in it: the creator would start
      // filling in a piece of work that is not the one they came for.
      const open = this.session.open();
      if (open === 'not_found') return 'context_not_found';
      if (open === 'unavailable') return 'context_unavailable';
    }

    if (!this.loaded()) return 'loading';

    return this.resumeOffered() ? 'resumable' : 'editing';
  });

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          const read = (key: string): string | null => {
            for (const params of maps) {
              const value = params.get(key);
              if (value) return value;
            }
            return null;
          };
          return { slug: read('workspaceSlug'), step: read('step'), contextId: read(CONTEXT_ROUTE_PARAM) };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, step, contextId }) => {
        if (slug === null) throw new Error('ContentPipelineShellComponent route is missing a workspaceSlug segment.');
        this.stepParam = step;
        if (slug !== this.workspaceSlug()) this.closeWorkspace(slug);
        this.routeContextId.set(contextId);
        this.applyRoute();
      });

    // The run is read once the memberships say who is asking, which is also what re-reads it when the workspace
    // or the context in the route changes, or a different person signs in on this browser.
    effect(() => {
      const owner = this.owner();
      const contextId = this.routeContextId();
      const key = `${this.ownerKey()}#${contextId ?? ''}`;
      if (owner === null || key === this.loadedKey) return;

      this.loadedKey = key;
      untracked(() => void this.open(owner, contextId));
    });

    // The context's answers are laid over the run whenever they change underneath it — on the first read, when
    // the creator loads the latest, or when another tab changed a field this one had not touched.
    effect(() => {
      const fields = this.session.fields();
      if (this.session.open() !== 'ready') return;

      untracked(() => {
        const draft = this.draft();
        const config = contentPipelineConfigWith(draft.config, fields);
        if (
          config.subject !== draft.config.subject ||
          config.channelKey !== draft.config.channelKey ||
          config.day !== draft.config.day ||
          config.concept !== draft.config.concept ||
          config.briefSource !== draft.config.briefSource ||
          config.brief !== draft.config.brief
        ) {
          this.draft.set({ ...draft, config });
        }
      });
    });

    // A run with no context gets one on its first answer — or on "Try again" after that failed.
    effect(() => {
      const contextId = this.session.context()?.id ?? null;
      if (contextId !== null) untracked(() => this.moveToContext(contextId));
    });

    // What has not reached the server is kept beside the run, and stops being kept once it has.
    effect(() => {
      this.session.unsent();
      untracked(() => this.persist());
    });
  }

  /** Every route node from this one up to the root, so a parameter can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }

  /**
   * Forget the workspace on screen, because the route now names another one.
   *
   * Everything read for the previous one goes, including the step index — nothing of one workspace may still be
   * showing while the next one's draft is being read.
   */
  private closeWorkspace(slug: string): void {
    this.workspaceSlug.set(slug);
    this.openTicket++;
    this.loadedKey = null;
    this.loadedOwner = null;
    this.resumeContextId = null;
    this.filedContextId = null;
    this.keptHere = false;
    this.session.close();
    this.loaded.set(false);
    this.filingFailed.set(false);
    this.draft.set(emptyContentPipelineDraft());
    this.currentIndex.set(null);
    this.resumeOffered.set(false);
    this.discarded.set(false);
    this.announcement.set('');
  }

  /**
   * Read one creator's run.
   *
   * With a context in the address, that context is the run: it is read from the server, and whatever this
   * device kept for it is laid beside it. Without one there are three things this device may hold, tried in
   * the order that loses nothing — a whole draft not yet on a context, which is filed first; a run already on
   * one, which is offered for resuming; or nothing, which is a clean start.
   */
  private async open(owner: ContentPipelineDraftOwner, contextId: string | null): Promise<void> {
    const ticket = ++this.openTicket;
    const slug = this.workspaceSlug();

    this.loadedOwner = owner;
    this.resumeContextId = null;
    this.filedContextId = null;
    this.keptHere = false;
    this.loaded.set(false);
    this.filingFailed.set(false);
    this.resumeOffered.set(false);
    this.discarded.set(false);

    if (contextId !== null) {
      await this.session.load(slug, contextId);
      if (ticket !== this.openTicket || this.session.open() !== 'ready') return;

      const { kept, discarded } = this.drafts.readKept(owner, contextId);
      const fields = this.session.fields();

      this.discarded.set(discarded);
      this.keptHere = kept !== null;
      this.draft.set({
        ...(kept?.draft ?? emptyContentPipelineDraft()),
        config: contentPipelineConfigWith((kept?.draft ?? emptyContentPipelineDraft()).config, fields),
      });
      this.drafts.rememberContext(owner, contextId);
      // Words typed here that never reached the server are the creator's latest, so they go on top and are sent.
      if (kept?.unsent) this.session.set({ ...fields, ...kept.unsent });

      this.loaded.set(true);
      this.applyRoute();
      return;
    }

    const { draft: unfiled, discarded } = this.drafts.read(owner);
    this.discarded.set(discarded);

    if (unfiled !== null && !isContentPipelineDraftEmpty(unfiled)) {
      await this.fileUnfiled(owner, unfiled, ticket);
      return;
    }

    const last = this.drafts.lastContextId(owner);
    const kept = last === null ? null : this.drafts.readKept(owner, last).kept;

    if (last !== null && kept !== null) {
      // Offered from what this device holds, without asking the server: Continue opens the context's own
      // address, and that read is the one that decides whether it is still there.
      this.resumeContextId = last;
      this.draft.set(kept.draft);
      this.resumeOffered.set(true);
    } else {
      this.draft.set(unfiled ?? emptyContentPipelineDraft());
      this.session.begin(slug, this.drafts.filing(owner), contentPipelineContextFields(this.draft()));
    }

    this.loaded.set(true);
    this.applyRoute();
  }

  /**
   * Put a whole draft found on this device onto a creative context, then open that context's own address.
   *
   * **The draft is not touched until the context exists.** If it cannot be made — offline, or the server is
   * having a bad moment — the creator is told their work is still here and offered another try; nothing is
   * shown as an empty run in the meantime.
   */
  private async fileUnfiled(owner: ContentPipelineDraftOwner, unfiled: ContentPipelineDraft, ticket: number): Promise<void> {
    this.draft.set(unfiled);
    this.session.begin(this.workspaceSlug(), this.drafts.filing(owner), contentPipelineContextFields(unfiled));

    const context = await this.session.file();
    if (ticket !== this.openTicket) return;

    if (context === null) {
      this.filingFailed.set(true);
      return;
    }

    this.moveToContext(context.id);
    await this.router.navigate(this.routeFor(unfiled.furthestStep), { replaceUrl: true });
  }

  /**
   * The run is on a context now: what this device keeps moves under that context's id.
   *
   * Reached two ways — from the filing above, and from the effect that watches for a context appearing, which
   * is what catches one made by the save notice's "Try again" — so it does its work once per context.
   */
  private moveToContext(contextId: string): void {
    const owner = this.loadedOwner;
    if (owner === null || this.routeContextId() !== null || contextId === this.filedContextId) return;

    this.filedContextId = contextId;
    this.keptHere = true;
    this.drafts.writeKept(owner, contextId, { draft: this.draft(), unsent: this.session.unsent() });
    this.drafts.clear(owner);
    this.drafts.rememberContext(owner, contextId);

    // A run that got its context while the creator was mid-thought keeps its screen: only the address
    // changes, to the one that will find this run again.
    if (this.loaded()) this.showAddress(this.routeFor(this.currentSlug() ?? FIRST_CONTENT_PIPELINE_STEP));
  }

  /** Change the address without navigating: the same screen, findable under its new name. */
  private showAddress(commands: readonly unknown[]): void {
    this.location.replaceState(this.router.serializeUrl(this.router.createUrlTree([...commands])));
  }

  /** Keep, for the context this run is on, what belongs on this device. */
  private persist(): void {
    const owner = this.loadedOwner;
    const contextId = this.session.context()?.id ?? null;
    if (owner === null || contextId === null || !this.loaded() || !this.keptHere) return;

    this.drafts.writeKept(owner, contextId, { draft: this.draft(), unsent: this.session.unsent() });
  }

  /**
   * Settle which step is on screen.
   *
   * A step nobody has reached yet, and a step slug this build does not know, both land on the furthest step
   * reached rather than on an error: the URL is navigation, and the remedy for a wrong one is to go somewhere
   * right.
   */
  private applyRoute(): void {
    // Nothing can be settled against a draft that has not been read: a step would be judged against the wrong
    // "furthest reached" and the creator would be sent back to the start of their own run.
    if (!this.loaded()) return;

    const furthest = this.draft().furthestStep;
    const furthestIndex = contentPipelineStepIndex(furthest);
    const asked = this.stepParam;

    if (!isContentPipelineStepSlug(asked) || contentPipelineStepIndex(asked) > furthestIndex) {
      if (asked !== furthest) {
        void this.router.navigate(this.routeFor(furthest), { replaceUrl: true });
        return;
      }
    }

    const index = contentPipelineStepIndex(asked);
    const settled = index === -1 ? furthestIndex : index;
    const previous = this.currentIndex();
    if (previous === settled) return;

    this.currentIndex.set(settled);
    this.focusHeading();

    // Announced on a *move*, not on arrival: the first step is announced by focus landing on its heading, and
    // a live region that already said something is what a screen reader has nothing else to tell it from.
    if (previous !== null) {
      this.announce(`${CONTENT_PIPELINE_STEPS[settled].label}. Step ${settled + 1} of ${this.total}.`);
    }
  }

  private routeFor(slug: ContentPipelineStepSlug): readonly unknown[] {
    const base = ['/', this.workspaceSlug(), 'workflows', 'content-pipeline'];

    // The context stays in the address from step to step, or Next would quietly drop the piece of work the
    // run was opened for.
    const contextId = this.contextId;

    return contextId === null ? [...base, slug] : [...base, CONTEXT_ROUTE_SEGMENT, contextId, slug];
  }

  /**
   * Move focus to the step's heading after a step change.
   *
   * Without it a keyboard or screen-reader user stays wherever the Continue button was and has to find the new
   * step by hand; the heading is `tabindex="-1"` for exactly this and never enters the tab order.
   */
  private focusHeading(): void {
    afterNextRender(
      () => {
        const heading = document.getElementById(HEADING_ID);
        heading?.focus();
      },
      { injector: this.injector },
    );
  }

  private announce(sentence: string): void {
    this.announcement.set(sentence);
  }

  /**
   * Keep the run and write it through, so what is on screen and what is kept never differ.
   *
   * Two destinations. The context's own answers go to the session, which sends them; the rest goes on this
   * device under the context's id. A run with no context yet is kept whole on the device and — once there is
   * something in it — filed on one.
   */
  private keep(next: ContentPipelineDraft): void {
    // Written under the owner whose run was *read*, never the one the route names right now: between a change
    // of workspace and the next render, the step on screen still belongs to the previous owner
    // (.claude/rules/tenancy.md).
    const owner = this.loadedOwner;
    if (owner === null || !this.loaded()) return;

    const previous = this.draft();
    // A brief the creator has not edited follows what it was chosen from, so correcting the description on
    // the first step corrects it too; one they have edited is left exactly as it is.
    const stamped: ContentPipelineDraft = {
      ...next,
      config: { ...next.config, ...contentPipelineBriefAfter(previous, next) },
      savedAt: new Date().toISOString(),
    };
    this.draft.set(stamped);
    this.session.set(this.fieldsFor(stamped, previous));

    if (this.session.context() !== null) {
      this.keptHere = true;
      this.persist();
      return;
    }

    this.drafts.write(owner, stamped);
    // Once there is something in it, and not again after a failure: that retry is the creator's to ask for,
    // or every keystroke typed while offline would be another request.
    if (!isContentPipelineDraftEmpty(stamped) && this.session.save() === 'saved') void this.session.file();
  }

  /**
   * The context's answers after a change to the run.
   *
   * The theme follows the idea: picking one takes that idea's theme, when the idea is for the run's day.
   * Changing the day drops it, because a theme belongs to a day. Otherwise it is left as the context has it —
   * a run handed over with a theme keeps it until the creator changes something that says otherwise.
   */
  private fieldsFor(next: ContentPipelineDraft, previous: ContentPipelineDraft): CreativeContextFields {
    const derived = contentPipelineContextFields(next);
    let weeklyThemeKey = this.session.fields().weeklyThemeKey;

    if (next.seed.accepted !== null && next.seed.accepted !== previous.seed.accepted) {
      weeklyThemeKey = derived.weeklyThemeKey;
    } else if (next.config.day !== previous.config.day) {
      weeklyThemeKey = null;
    }

    return { ...derived, weeklyThemeKey };
  }

  protected onConfigChanged(config: ContentPipelineConfig): void {
    this.keep({ ...this.draft(), config });
  }

  protected onDraftChanged(next: ContentPipelineDraft): void {
    this.keep(next);
  }

  protected onRecipeNamed(recipe: DamKeepRecipe | null): void {
    this.namedRecipe.set(recipe);
  }

  protected onAnnounced(sentence: string): void {
    this.announce(sentence);
  }

  /** Open the kept run at its context's own address, where the context itself is read. */
  protected resume(): void {
    void this.router.navigate(this.routeFor(this.draft().furthestStep));
  }

  protected back(): void {
    const index = this.currentIndex();
    if (index === null || index === 0) return;

    void this.session.flush();
    void this.router.navigate(this.routeFor(CONTENT_PIPELINE_STEP_SLUGS[index - 1]));
  }

  /**
   * Forward one step, widening how far the creator has got.
   *
   * `furthestStep` only ever moves forward: coming back to an earlier step to change an answer must not take
   * away the steps already reached.
   */
  protected continue(): void {
    const index = this.currentIndex();
    if (index === null || !this.canContinue()) return;

    const next = CONTENT_PIPELINE_STEP_SLUGS[index + 1];
    const draft = this.draft();
    if (contentPipelineStepIndex(next) > contentPipelineStepIndex(draft.furthestStep)) {
      this.keep({ ...draft, furthestStep: next });
    }

    // Sent now rather than after the wait: the next step may be a new screen that reads the context.
    void this.session.flush();
    void this.router.navigate(this.routeFor(next));
  }

  /** Leave. An answer still waiting to be sent goes now; everything else was kept as it was made. */
  protected async saveAndExit(): Promise<void> {
    await this.session.flush();
    await this.router.navigate(['/', this.workspaceSlug(), 'workflows']);
  }

  /**
   * Throw the draft away and begin again.
   *
   * The one action here that loses work, so it is the one that asks — through `ConfirmService`, where Escape and
   * the backdrop both mean "stay" (.claude/rules/design-system.md).
   */
  protected async startOver(): Promise<void> {
    const discard = await this.confirm.confirm({
      title: 'Start this over?',
      message: 'Everything you have filled in for this pipeline will be thrown away, including the idea you picked.',
      confirmLabel: 'Throw it away',
      cancelLabel: 'Keep what I have',
    });
    if (!discard) return;

    // Re-read: a different workspace could have arrived behind the dialog.
    const owner = this.loadedOwner;
    if (owner === null || !this.loaded()) return;

    // What this device keeps for the run goes, and the run stops being the one offered here. Its context is
    // left as it is: it may be what a concept or a prompt was handed over on, and nothing here archives it.
    const contextId = this.contextId;
    if (contextId !== null) this.drafts.clearKept(owner, contextId);
    this.drafts.clear(owner);
    this.drafts.forgetContext(owner);
    this.drafts.filing(owner).clear();

    this.resumeContextId = null;
    this.filedContextId = null;
    this.keptHere = false;
    this.draft.set(emptyContentPipelineDraft());
    this.resumeOffered.set(false);
    this.discarded.set(false);
    this.announce('Started over.');

    const first = FIRST_CONTENT_PIPELINE_STEP;

    if (this.routeContextId() !== null) {
      // Out of the context's address altogether, to the pipeline's own — which is a new screen.
      await this.router.navigate(['/', this.workspaceSlug(), 'workflows', 'content-pipeline', first]);
      return;
    }

    this.session.begin(this.workspaceSlug(), this.drafts.filing(owner));
    // A run filed in place changed the address without the router knowing, so it is put back the same way.
    this.showAddress(this.routeFor(first));
    if (this.stepParam === first) this.applyRoute();
    else await this.router.navigate(this.routeFor(first));
  }

  /** Try once more to put the work found on this device onto a context. */
  protected retryFiling(): void {
    const owner = this.owner();
    if (owner === null) return;

    void this.open(owner, null);
  }

  /** Read the context in the address again, after a read that failed. */
  protected retryContext(): void {
    const owner = this.owner();
    const contextId = this.routeContextId();
    if (owner === null || contextId === null) return;

    void this.open(owner, contextId);
  }

  protected readonly startLink = computed(() => ['/', this.workspaceSlug(), 'workflows', 'content-pipeline']);

  protected retryMemberships(): void {
    void this.memberships.load();
  }
}
