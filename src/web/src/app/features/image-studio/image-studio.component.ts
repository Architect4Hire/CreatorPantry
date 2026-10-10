import { Location } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpCardComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  ContentPipelineConfig,
  ContentPipelineDocumentRef,
  ContentPipelineDraft,
  ContentPipelineImagesState,
  ContentPipelinePromptState,
  FIRST_CONTENT_PIPELINE_STEP,
  emptyContentPipelinePostsState,
  contentPipelineBriefAfter,
  contentPipelineConfigWith,
} from '../../models/content-pipeline.models';
import { CreativeContextFields, EMPTY_CREATIVE_CONTEXT_FIELDS } from '../../models/creative-context-fields.models';
import { linkedRecipeOf } from '../../models/creative-context.models';
import { DamCreatedAsset, DamKeepRecipe } from '../../models/dam-asset.models';
import { avoidTextFor } from '../../models/generated-image.models';
import { ImageStudioDraft, emptyImageStudioDraft, isImageStudioDraftEmpty } from '../../models/image-studio.models';
import { ImageStudioDraftOwner, ImageStudioDraftService } from '../../services/image-studio-draft.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { CreativeContextSaveNoticeComponent } from '../../shared/creative-context-save-notice/creative-context-save-notice.component';
import { RecipePickerComponent } from '../../shared/recipe-picker/recipe-picker.component';
import { CONTEXT_ROUTE_PARAM, HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';
import { ContentPipelineConceptPanelComponent } from '../content-pipeline/content-pipeline-concept-panel.component';
import { ContentPipelineDocumentPickerComponent } from '../content-pipeline/content-pipeline-document-picker.component';
import { ContentPipelineIdeaStepComponent } from '../content-pipeline/content-pipeline-idea-step.component';
import { ContentPipelinePromptPanelComponent } from '../content-pipeline/content-pipeline-prompt-panel.component';
import { ContentPipelineReferencePanelComponent } from '../content-pipeline/content-pipeline-reference-panel.component';
import { ContentPipelineSetupStepComponent } from '../content-pipeline/content-pipeline-setup-step.component';
import {
  GeneratedImageRunComponent,
  GeneratedImageRunLibrary,
  GeneratedImageRunWording,
} from '../content-pipeline/generated-image-run.component';

/**
 * `loading` and `unavailable` are how reading the creator's memberships can end; `not_found` and `read_only`
 * are the two refusals; `ready` is the studio itself.
 *
 * Three are about the work's creative context (AF.3.1). `context_not_found` and `context_unavailable` are how
 * reading the one the address names can end — and neither opens as an empty studio. `filing_failed` is work
 * found on this device that could not be put on a context: it stays where it is until that works.
 */
type Phase =
  | 'loading'
  | 'unavailable'
  | 'not_found'
  | 'read_only'
  | 'context_not_found'
  | 'context_unavailable'
  | 'filing_failed'
  | 'ready';

/** The parts of the page a creator can jump between, in the order the work usually happens. */
const SECTIONS: readonly CpAnchorNavItem[] = [
  { targetId: 'cp-studio-brief', label: 'The brief' },
  { targetId: 'cp-studio-idea', label: 'An idea' },
  { targetId: 'cp-studio-look', label: 'The look' },
  { targetId: 'cp-studio-extras', label: 'Extras' },
  { targetId: 'cp-studio-prompt', label: 'The prompt' },
  { targetId: 'cp-studio-images-make', label: 'The pictures' },
];

/** The run's sentences, said for a page where everything it refers to is above it rather than a step back. */
const STUDIO_RUN_WORDING: GeneratedImageRunWording = {
  makeIntro: 'This is the prompt above, exactly as it will be sent. Change it there if it is not right yet.',
  noPrompt: 'There is no prompt yet. Write one above, or have one written for you.',
  tooLongRemedy: 'Shorten it above.',
  countRemedy: 'Change that in the brief.',
  forbidden: 'You do not have permission to make pictures in this workspace. Ask an Owner or Editor to make them.',
  chooseHeading: 'Save the ones worth keeping',
  chooseIntro: 'Saving a picture puts it in your library for good, with what you want to say about it.',
  keepNote:
    'A picture you save is yours from then on and can be found in your library. One you do not save is not kept.',
  expiryRemedy: 'so save anything you want to keep.',
};

/**
 * The Image Studio: from a description of a shot to the pictures worth keeping (IMAGE-UI-001 through 004).
 *
 * **One page, not a journey.** It is the client for the same four contracts as the Content Pipeline's prompt
 * and images steps — looks planned for a shot (IMG-001), a prompt composed and then the creator's (IMG-002), a
 * reference photograph read (IMG-004), and a run of pictures asked for, looked at and declined
 * (IMG-003, IMG-005/006) — without a posts step after. Every section is on screen at once, because a creator
 * who already knows the prompt they want has no use for being walked to it.
 *
 * **An idea is offered, and never required.** The pipeline's idea step sits between the brief and the look:
 * the typed name is read into a cuisine, a dish type and a method the creator can change, an idea can be
 * suggested around them, and each part of it changed on its own. Picking one offers the same choice the
 * pipeline does — plan from the description, the idea, or both — and the looks are planned from the answer.
 * The day and its theme are not shown as parts: the studio plans no week.
 *
 * **Which is the one behaviour that differs from the pipeline: the prompt box is always here.** A prompt the
 * creator types themselves is as finished as one written for them, and needs no look picked first. Having one
 * *written* still does, because that is what IMG-002 composes from.
 *
 * **What the work is about lives on its creative context** (AF.3.1): the channel and the picture the creator
 * has in mind are read from the server and saved back as they change, through `CreativeContextSession`, so the
 * same work opens the same on another device. A context's day and theme are not asked here and are left as
 * they are. What stays on this device (`ImageStudioDraftService`, keyed by the context's id) is the rest —
 * request ids, the creator's pick, their scene lines and their prompt, which the context has no field for yet.
 * Work gets its context on the first answer, not on arrival.
 *
 * **It owns the kept draft; the panels own their fields.** Each is handed its part and hands back the next one,
 * so there is exactly one copy of the creator's answers and what is on screen is always what is kept. The
 * panels are the pipeline's own, imported rather than copied — their request tracking, staleness guards and
 * allowance handling are the same work on both screens.
 *
 * **A picture worth keeping is saved to the library from here** (AF.4.2, FLU-005). Saving is the one decision
 * the run offers on this page — there is no separate keeper to mark first — and it is per picture, through the
 * shared save form. The asset is then named on the work's creative context, so a picture made here is one the
 * rest of the product can find. It does not save a prompt to the prompt library or write a post: each of those
 * is a separate action with its own contract, and neither is on this page.
 *
 * **Nothing here cancels a generation, because no route does.** "Stop checking" stops this page asking; leaving
 * the page, changing workspace and starting over all abandon whatever was in flight, and an abandoned answer
 * never lands on what the creator is looking at next.
 *
 * **Leaving costs nothing, so leaving is not confirmed.** Every change is kept as it is made. Starting over
 * does ask, because that is the one action that throws the draft away.
 *
 * **The workspace comes from the route and the creator's own membership.** An unknown workspace and one they
 * cannot reach answer alike, so neither discloses that the other exists (.claude/rules/tenancy.md).
 */
@Component({
  selector: 'cp-image-studio',
  standalone: true,
  imports: [
    RouterLink,
    CpAnchorNavComponent,
    CpButtonComponent,
    CpCardComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CreativeContextSaveNoticeComponent,
    RecipePickerComponent,
    ContentPipelineConceptPanelComponent,
    ContentPipelineDocumentPickerComponent,
    ContentPipelineIdeaStepComponent,
    ContentPipelinePromptPanelComponent,
    ContentPipelineReferencePanelComponent,
    ContentPipelineSetupStepComponent,
    GeneratedImageRunComponent,
  ],
  templateUrl: './image-studio.component.html',
  styleUrl: './image-studio.component.css',
  providers: [CreativeContextSession],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ImageStudioComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  protected readonly session = inject(CreativeContextSession);
  private readonly drafts = inject(ImageStudioDraftService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirm = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly sections = SECTIONS;
  protected readonly runWording = STUDIO_RUN_WORDING;

  readonly workspaceSlug = signal('');
  readonly draft = signal<ImageStudioDraft>(emptyImageStudioDraft());
  readonly announcement = signal('');
  /** True when something was stored here that could not be read, so the creator is told rather than puzzled. */
  readonly discarded = signal(false);
  /** The section last jumped to, so the navigation can say where the creator is. */
  protected readonly activeSection = signal<string | null>(null);

  /** False until this creator's work has been read — its context from the server, the rest from this device. */
  private readonly loaded = signal(false);
  /** True when work found on this device could not be put on a context. It is still on the device. */
  private readonly filingFailed = signal(false);
  /** True when this device refused to keep work that has no context yet, so nothing claims it was kept. */
  private readonly deviceRefused = signal(false);
  /** The creative context the address names, or null for the studio's bare address. */
  private readonly routeContextId = signal<string | null>(null);
  /** What is currently loaded — an owner and the context the address names — so it is not read twice. */
  private loadedKey: string | null = null;
  /** That owner. Every write goes under it — see `keep`. */
  private loadedOwner: ImageStudioDraftOwner | null = null;
  /** Raised by each open, so an answer to an earlier one lands nowhere. */
  private openTicket = 0;
  /** True once something has been kept for this context here, so merely opening one does not leave a record. */
  private keptHere = false;
  /** The context this work was filed on since it was opened, so that is recorded once. */
  private filedContextId: string | null = null;
  /** Raised by Start over, so the panels are rebuilt rather than left holding the answers just thrown away. */
  private readonly epoch = signal(0);

  readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  /**
   * Whose studio this is: the workspace by id, and the membership reading it.
   *
   * Null until the creator's memberships have been read, which is why the draft cannot be loaded from the route
   * alone — the slug names a workspace, and a kept draft belongs to one *person* in one workspace.
   */
  readonly owner = computed<ImageStudioDraftOwner | null>(() => {
    const membership = this.membership();

    return membership === null
      ? null
      : { workspaceId: membership.workspaceId, membershipId: membership.membershipId };
  });

  private readonly ownerKey = computed(() => {
    const owner = this.owner();

    return owner === null ? '' : `${owner.workspaceId}.${owner.membershipId}`;
  });

  /**
   * A list of one key, so the template can build the panels with `@for` and destroy them when it changes.
   *
   * The panels resume their requests once, when they are built, and each holds a tracker with the last answer
   * in it. Swapping a new draft underneath them — another workspace's, another member's, or the empty one after
   * Start over — would leave the previous answers on screen under the new draft, and the prompt panel would
   * write the old composition straight back into it. Rebuilding is what makes that impossible
   * (.claude/rules/tenancy.md).
   */
  protected readonly panelKeys = computed(() => [`${this.ownerKey()}#${this.epoch()}`]);

  /**
   * Derived rather than set, so the screen cannot be left claiming a phase the data has moved past.
   *
   * **A Viewer is stopped at the door rather than led through.** Every ask on this page needs the Contributor
   * role, so a Viewer would meet a refusal behind each button; saying so once is kinder than that.
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
      // An id that does not resolve is said, never shown as a studio with nothing in it.
      const open = this.session.open();
      if (open === 'not_found') return 'context_not_found';
      if (open === 'unavailable') return 'context_unavailable';
    }

    return this.loaded() ? 'ready' : 'loading';
  });

  /** The recipe this work is about, read from its context: what both picture requests name. */
  protected readonly linkedRecipe = computed(() => linkedRecipeOf(this.session.context()));

  /**
   * That recipe with its title, as the picker resolved it.
   *
   * The context names a recipe by id, and a save form offering the link has to show the creator *which*
   * recipe. The picker reads it already, so it is taken from there rather than read a second time here.
   */
  private readonly namedRecipe = signal<DamKeepRecipe | null>(null);

  /** That recipe's title, for the brief's hint about which answer the work is planned around. */
  protected readonly linkedRecipeTitle = computed(() => this.namedRecipe()?.title ?? null);

  /**
   * What the run needs to file a picture in the library (AF.4.2, FLU-005).
   *
   * The title it starts from is the work's own if the creator has named it, else the recipe's — a fact about
   * what the picture is of, never the prompt, which describes what was asked for rather than what came back
   * (.claude/rules/media.md). The references are where a picture saved in an earlier visit is found again.
   */
  protected readonly runLibrary = computed<GeneratedImageRunLibrary>(() => {
    const recipe = this.namedRecipe();
    const context = this.session.context();
    const working = context?.workingTitle?.trim() ?? '';

    return {
      defaultTitle: working !== '' ? working : recipe?.title ?? '',
      defaultRecipe: recipe,
      references: context?.references ?? [],
    };
  });

  protected readonly prompt = computed(() => this.draft().prompt);
  protected readonly config = computed(() => this.draft().config);

  /** The work in the shape the pipeline's idea step reads. The same answers; nothing is copied anywhere. */
  protected readonly ideaDraft = computed(() => ImageStudioComponent.asPipeline(this.draft()));

  /**
   * What the looks are planned from: the brief the creator chose once they have picked an idea, and their
   * description as written until then.
   */
  protected readonly lookBrief = computed(() => {
    const { briefSource, brief, concept } = this.config();

    return briefSource !== null && brief.trim() !== '' ? brief : concept;
  });

  /**
   * What the run will tell the provider to avoid, or null.
   *
   * It came with the composition the creator was offered, so it is a choice they already saw — and the run
   * shows it beside the prompt before anything is sent.
   */
  protected readonly avoidText = computed(() => avoidTextFor(this.prompt().generated?.avoid ?? []));

  /** A linked recipe is something to throw away too, even with nothing else filled in. */
  protected readonly canStartOver = computed(() => !isImageStudioDraftEmpty(this.draft()) || this.linkedRecipe() !== null);

  /**
   * Where the creator's answers are, said plainly.
   *
   * Two places, and it says both: what the work is about is saved to the workspace, and the rest is on this
   * device. Anything that needs the creator's attention is the save notice's to say, not this line's.
   */
  readonly savedText = computed(() => {
    const context = this.session.context();
    if (context === null) {
      return this.deviceRefused()
        ? 'This is not being kept on this device right now. Signing in again will keep what you do next.'
        : 'What you fill in is kept on this device as you go.';
    }

    const save = this.session.save();
    if (save === 'pending' || save === 'saving') return 'Saving…';
    if (save !== 'saved') return 'Not saved yet.';

    const at = new Date(context.updatedAt);
    const rest = 'Your prompt, your picks and your pictures are kept on this device.';
    if (Number.isNaN(at.getTime())) return `Saved. ${rest}`;

    return `Saved at ${new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(at)}. ${rest}`;
  });

  protected readonly startLink = computed(() => ['/', this.workspaceSlug(), 'image-studio']);

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
          return { slug: read('workspaceSlug'), contextId: read(CONTEXT_ROUTE_PARAM) };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, contextId }) => {
        if (slug === null) throw new Error('ImageStudioComponent route is missing a workspaceSlug segment.');
        if (slug !== this.workspaceSlug()) this.closeWorkspace(slug);
        this.routeContextId.set(contextId);
      });

    // The work is read once the memberships say who is asking, which is also what re-reads it when the
    // workspace or the context in the route changes, or a different person signs in on this browser.
    effect(() => {
      const owner = this.owner();
      const contextId = this.routeContextId();
      const key = `${this.ownerKey()}#${contextId ?? ''}`;
      if (owner === null || key === this.loadedKey) return;

      this.loadedKey = key;
      untracked(() => void this.open(owner, contextId));
    });

    // The context's answers are laid over the work whenever they change underneath it — on the first read,
    // when the creator loads the latest, or when another tab changed a field this one had not touched.
    effect(() => {
      const fields = this.session.fields();
      if (this.session.open() !== 'ready') return;

      untracked(() => {
        const draft = this.draft();
        if (
          fields.workingTitle !== draft.config.subject ||
          fields.channelKey !== draft.config.channelKey ||
          fields.pictureBrief !== draft.config.concept ||
          fields.briefSource !== draft.config.briefSource ||
          fields.workingBrief !== draft.config.brief
        ) {
          this.draft.set({ ...draft, config: this.configWith(draft.config, fields) });
        }
      });
    });

    // Work with no context gets one on its first answer — or on "Try again" after that failed.
    effect(() => {
      const contextId = this.session.context()?.id ?? null;
      if (contextId !== null) untracked(() => this.moveToContext(contextId));
    });

    // What has not reached the server is kept beside the work, and stops being kept once it has.
    effect(() => {
      this.session.unsent();
      untracked(() => this.persist());
    });
  }

  /**
   * The studio's draft as a pipeline one, for the components the two screens share.
   *
   * The posts block is empty and stays empty: the Image Studio is about pictures and has no posts step, so
   * there is no request to follow and nothing unsaved. Supplied rather than left out, so the shared type
   * stays one shape.
   */
  private static asPipeline(draft: ImageStudioDraft): ContentPipelineDraft {
    return { ...draft, posts: emptyContentPipelinePostsState(), furthestStep: FIRST_CONTENT_PIPELINE_STEP };
  }

  /** The config with the context's name, channel and picture. The day is not asked here, so none is shown. */
  private configWith(config: ContentPipelineConfig, fields: CreativeContextFields): ContentPipelineConfig {
    return { ...contentPipelineConfigWith(config, fields), day: null };
  }

  /** Every route node from this one up to the root, so the slug can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }

  /**
   * Forget the workspace on screen, because the route now names another one.
   *
   * Everything read for the previous one goes — nothing of one workspace may still be showing while the next
   * one's draft is being read.
   */
  private closeWorkspace(slug: string): void {
    this.workspaceSlug.set(slug);
    this.openTicket++;
    this.loadedKey = null;
    this.loadedOwner = null;
    this.keptHere = false;
    this.filedContextId = null;
    this.session.close();
    this.loaded.set(false);
    this.filingFailed.set(false);
    this.deviceRefused.set(false);
    this.draft.set(emptyImageStudioDraft());
    this.discarded.set(false);
    this.announcement.set('');
    this.activeSection.set(null);
  }

  /**
   * Read one creator's work.
   *
   * With a context in the address, that context is the work: it is read from the server, and whatever this
   * device kept for it is laid beside it. Without one there are three things this device may hold, tried in
   * the order that loses nothing — a whole draft not yet on a context, which is filed first; work already on
   * one, which is opened at its own address; or nothing, which is a clean start.
   */
  private async open(owner: ImageStudioDraftOwner, contextId: string | null): Promise<void> {
    const ticket = ++this.openTicket;
    const slug = this.workspaceSlug();

    this.loadedOwner = owner;
    this.keptHere = false;
    this.filedContextId = null;
    this.loaded.set(false);
    this.filingFailed.set(false);
    this.deviceRefused.set(false);
    this.discarded.set(false);

    if (contextId !== null) {
      await this.session.load(slug, contextId);
      if (ticket !== this.openTicket || this.session.open() !== 'ready') return;

      const { kept, discarded } = this.drafts.readKept(owner, contextId);
      const fields = this.session.fields();
      const draft = kept?.draft ?? emptyImageStudioDraft();

      this.discarded.set(discarded);
      this.keptHere = kept !== null;
      this.draft.set({ ...draft, config: this.configWith(draft.config, fields) });
      this.drafts.rememberContext(owner, contextId);
      // Words typed here that never reached the server are the creator's latest, so they go on top and are sent.
      if (kept?.unsent) this.session.set({ ...fields, ...kept.unsent });

      this.loaded.set(true);
      return;
    }

    const { draft: unfiled, discarded } = this.drafts.read(owner);
    this.discarded.set(discarded);

    if (unfiled !== null && !isImageStudioDraftEmpty(unfiled)) {
      // Not touched until the context exists. If it cannot be made, the creator is told the work is still
      // here and offered another try; nothing is shown as an empty studio in the meantime.
      this.draft.set(unfiled);
      this.session.begin(slug, this.drafts.filing(owner), {
        ...EMPTY_CREATIVE_CONTEXT_FIELDS,
        channelKey: unfiled.config.channelKey,
        pictureBrief: unfiled.config.concept,
        briefSource: unfiled.config.briefSource,
        workingBrief: unfiled.config.brief,
      });

      const context = await this.session.file();
      if (ticket !== this.openTicket) return;

      if (context === null) {
        this.filingFailed.set(true);
        return;
      }

      this.moveToContext(context.id);
      await this.router.navigate(['/', slug, ...HANDOFF_ROUTES.imageStudio(context.id)], { replaceUrl: true });
      return;
    }

    const last = this.drafts.lastContextId(owner);
    if (last !== null && this.drafts.readKept(owner, last).kept !== null) {
      // The studio has no step to offer a resume from: the work is simply opened, at the address that reads it.
      await this.router.navigate(['/', slug, ...HANDOFF_ROUTES.imageStudio(last)], { replaceUrl: true });
      return;
    }

    this.draft.set(emptyImageStudioDraft());
    this.session.begin(slug, this.drafts.filing(owner));
    this.loaded.set(true);
  }

  /**
   * The context's answers for this work: its name, channel and picture from here, its day and theme left alone.
   */
  private fieldsFor(draft: ImageStudioDraft): CreativeContextFields {
    return {
      ...this.session.fields(),
      workingTitle: draft.config.subject,
      channelKey: draft.config.channelKey,
      pictureBrief: draft.config.concept,
      briefSource: draft.config.briefSource,
      workingBrief: draft.config.brief,
    };
  }

  /**
   * The work is on a context now: what this device keeps moves under that context's id.
   *
   * Reached from the filing above and from the effect that watches for a context appearing — which is what
   * catches one made by the save notice's "Try again" — so it does its work once per context.
   */
  private moveToContext(contextId: string): void {
    const owner = this.loadedOwner;
    if (owner === null || this.routeContextId() !== null || contextId === this.filedContextId) return;

    this.filedContextId = contextId;
    this.keptHere = true;
    this.drafts.writeKept(owner, contextId, { draft: this.draft(), unsent: this.session.unsent() });
    this.drafts.clear(owner);
    this.drafts.rememberContext(owner, contextId);

    // Work that got its context while the creator was mid-thought keeps its screen: only the address changes,
    // to the one that will find it again.
    if (this.loaded()) this.showAddress(['/', this.workspaceSlug(), ...HANDOFF_ROUTES.imageStudio(contextId)]);
  }

  /** Change the address without navigating: the same screen, findable under its new name. */
  private showAddress(commands: readonly unknown[]): void {
    this.location.replaceState(this.router.serializeUrl(this.router.createUrlTree([...commands])));
  }

  /** Keep, for the context this work is on, what belongs on this device. */
  private persist(): void {
    const owner = this.loadedOwner;
    const contextId = this.session.context()?.id ?? null;
    if (owner === null || contextId === null || !this.loaded() || !this.keptHere) return;

    this.drafts.writeKept(owner, contextId, { draft: this.draft(), unsent: this.session.unsent() });
  }

  /**
   * Keep the work and write it through, so what is on screen and what is kept never differ.
   *
   * Two destinations. The context's own answers go to the session, which sends them; the rest goes on this
   * device under the context's id. Work with no context yet is kept whole on the device and — once there is
   * something in it — filed on one.
   */
  private keep(next: ImageStudioDraft): void {
    // Written under the owner whose work was *read*, never the one the route names right now. Between a change
    // of workspace and the next render, the panels on screen still belong to the previous owner; anything one
    // of them emits in that gap would otherwise be stored under the new owner's key (.claude/rules/tenancy.md).
    const owner = this.loadedOwner;
    if (owner === null || !this.loaded()) return;

    // A brief chosen from the description or the idea follows what it was chosen from, as it does in the
    // pipeline: correcting the description corrects it, and un-picking the idea lets go of it.
    const followed = contentPipelineBriefAfter(
      ImageStudioComponent.asPipeline(this.draft()),
      ImageStudioComponent.asPipeline(next),
    );
    const stamped: ImageStudioDraft = {
      ...next,
      config: { ...next.config, ...followed },
      savedAt: new Date().toISOString(),
    };
    this.draft.set(stamped);
    this.session.set(this.fieldsFor(stamped));

    if (this.session.context() !== null) {
      this.keptHere = true;
      this.persist();
      return;
    }

    // A refused write — the session has lapsed — keeps the edit on screen but not the claim that it was kept.
    this.deviceRefused.set(!this.drafts.write(owner, stamped));
    // Once there is something in it, and not again after a failure: that retry is the creator's to ask for,
    // or every keystroke typed while offline would be another request.
    if (!isImageStudioDraftEmpty(stamped) && this.session.save() === 'saved') void this.session.file();
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }

  /** Read again — the context in the address, or the work on this device that could not be filed. */
  protected retryOpen(): void {
    const owner = this.owner();
    if (owner === null) return;

    void this.open(owner, this.routeContextId());
  }

  protected onConfig(config: ContentPipelineConfig): void {
    // The day is not asked here, so whatever a shared panel hands back, none is kept.
    this.keep({ ...this.draft(), config: { ...config, day: null } });
  }

  /** The idea step hands back the whole work; what it may have changed is the idea and the answers beside it. */
  protected onIdea(next: ContentPipelineDraft): void {
    this.keep({ ...this.draft(), config: { ...next.config, day: null }, seed: next.seed });
  }

  protected onPrompt(prompt: ContentPipelinePromptState): void {
    this.keep({ ...this.draft(), prompt });
  }

  protected onBrief(brief: ContentPipelineDocumentRef | null): void {
    this.onPrompt({ ...this.prompt(), brief });
  }

  protected onImages(images: ContentPipelineImagesState): void {
    this.keep({ ...this.draft(), images });
  }

  protected onAnnounced(sentence: string): void {
    this.announcement.set(sentence);
  }

  protected onRecipeNamed(recipe: DamKeepRecipe | null): void {
    this.namedRecipe.set(recipe);
  }

  /**
   * A picture made here is in the library: the work is made to name the asset it became (AF.4.2).
   *
   * **The save and the naming are separate, and said separately.** The picture is in the library the moment
   * the route answers; putting the asset on this piece of work is a second write, and one that fails leaves a
   * saved picture that this work does not point at. The creator is told exactly that rather than left to
   * believe either more or less than happened.
   */
  protected async onSaved(asset: DamCreatedAsset): Promise<void> {
    const outcome = await this.session.addReference({
      kind: 'DamAsset',
      // A picture this work *made*, not one it takes cues from — so the reference panel never offers it back
      // as inspiration the creator did not choose, and replacing the cue cannot remove it (AF.4.3).
      purpose: 'Keeper',
      mediaAssetId: asset.id,
      mediaAssetVersionNumber: asset.currentVersionNumber,
    });
    if (outcome === 'saved' || outcome === 'duplicate') return;

    this.announcement.set(
      `“${asset.title}” is in your library, but this piece of work could not be made to point at it just now.`,
    );
  }

  /**
   * Go to one part of the page.
   *
   * The navigation leaves scrolling and focus to its consumer. `scrollIntoView` with no `behavior` lets the
   * stylesheet decide, which is how global.css honours `prefers-reduced-motion`; focus then lands on the
   * section itself, so a keyboard or screen-reader user arrives where a sighted one was taken.
   */
  protected onSectionActivated(item: CpAnchorNavItem): void {
    this.activeSection.set(item.targetId);

    const target = this.host.nativeElement.querySelector<HTMLElement>(`#${CSS.escape(item.targetId)}`);
    target?.scrollIntoView({ block: 'start' });
    target?.focus({ preventScroll: true });
  }

  /**
   * Throw the draft away and begin again.
   *
   * The one action here that loses work, so it is the one that asks — through `ConfirmService`, where Escape and
   * the backdrop both mean "stay" (.claude/rules/design-system.md). Pictures already made are not declined by
   * this: they stay where they are until they are collected, and the sentence says so rather than implying a
   * clean-up that does not happen.
   */
  protected async startOver(): Promise<void> {
    const owner = this.owner();
    if (owner === null || !this.canStartOver()) return;

    const discard = await this.confirm.confirm({
      title: 'Start this over?',
      message:
        'Everything you have filled in here will be thrown away, including your prompt. Pictures already made are not removed by this — they simply stop being shown here.',
      confirmLabel: 'Throw it away',
      cancelLabel: 'Keep what I have',
    });
    if (!discard) return;

    // Re-read: a different workspace could have arrived behind the dialog, and clearing the owner captured
    // before it would throw away the wrong creator's work.
    const current = this.owner();
    if (current === null || current.workspaceId !== owner.workspaceId || current.membershipId !== owner.membershipId) {
      return;
    }

    // What this device keeps for the work goes, and it stops being the work opened here. Its context is left
    // as it is: it may be what a concept or a prompt was handed over on, and nothing here archives it.
    const contextId = this.session.context()?.id ?? this.routeContextId();
    if (contextId !== null) this.drafts.clearKept(current, contextId);
    this.drafts.clear(current);
    this.drafts.forgetContext(current);
    this.drafts.filing(current).clear();

    // The same screen, emptied, under the studio's own address — not a navigation, which would build a new
    // page and lose the sentence that says what just happened. So the context the route named is let go of by
    // hand, and marked as already read so that letting go does not open anything.
    this.loadedKey = `${this.ownerKey()}#`;
    this.routeContextId.set(null);
    this.keptHere = false;
    this.filedContextId = null;
    this.session.begin(this.workspaceSlug(), this.drafts.filing(current));
    this.showAddress(this.startLink());
    this.draft.set(emptyImageStudioDraft());
    this.deviceRefused.set(false);
    this.discarded.set(false);
    this.activeSection.set(null);
    this.epoch.update((value) => value + 1);
    this.announcement.set('Started over. Nothing is filled in.');
  }
}
