import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
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
import { combineLatest, map } from 'rxjs';
import {
  CpBackLinkComponent,
  CpButtonComponent,
  CpCardComponent,
  CpFactComponent,
  CpFactsComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpGeneratedLabelComponent,
  CpLightboxComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpProgressComponent,
  CpSkeletonComponent,
  CpStep,
  CpStepperComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { injectRouteTitle } from '../../core/route-title';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import { DAM_ASSET_LIMITS, encodeDamKeep } from '../../models/dam-asset.models';
import {
  GENERATED_IMAGE_PROMPT_MAX_LENGTH,
  StagedImage,
  avoidTextFor,
  generatedImageFailureSentence,
  isStagedImageActionable,
} from '../../models/generated-image.models';
import { decodeComposedImagePrompt } from '../../models/image-prompt.models';
import {
  PHOTOGRAPHY_CREATOR_CONCEPT_MAX_LENGTH,
  PhotographyShotKind,
  decodePhotographyConcepts,
} from '../../models/photography-concept.models';
import { RecipeDraft, draftFromProposal, pictureBriefFromDraft } from '../../models/recipe-draft.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService } from '../../services/generated-image.service';
import { ImagePromptService } from '../../services/image-prompt.service';
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { RecipeAssetLinkService } from '../../services/recipe-asset-link.service';
import { RecipeDraftService } from '../../services/recipe-draft.service';
import { RecipeService } from '../../services/recipe.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { AiOperationStatusComponent } from '../ai/ai-operation-status.component';
import { AiOperationPhase, AiOperationTracker } from '../ai/ai-operation-tracker';
import { IdempotencyKey } from '../content-pipeline/content-pipeline-idempotency';
import { ContentPipelineStagedImageComponent } from '../content-pipeline/content-pipeline-staged-image.component';
import { GeneratedImageTracker } from '../content-pipeline/generated-image-tracker';

/** How reading the creator's memberships can end, as the Workflows page has it. */
type Phase = 'loading' | 'unavailable' | 'not_found' | 'read_only' | 'ready';

type StepId = 'name' | 'making' | 'review';

const STEPS: readonly { readonly id: StepId; readonly label: string }[] = [
  { id: 'name', label: 'Name your dish' },
  { id: 'making', label: 'Making it' },
  { id: 'review', label: 'Check and save' },
];

/** `AiPolicy.PhotographyDishNameMaxLength`, the tightest of the three routes the name is sent to. */
export const QUICK_RECIPE_NAME_MAX_LENGTH = 200;

const HEADING_ID = 'cp-quick-recipe-step-heading';
const NAME_INPUT_ID = 'cp-quick-recipe-name';

/** Where the picture has got to, across the three requests it takes to make one. */
type PictureView =
  | { readonly kind: 'none' }
  | { readonly kind: 'working'; readonly sentence: string }
  | { readonly kind: 'ready'; readonly image: StagedImage }
  /** No picture, and why. Never a reason the recipe cannot be saved. */
  | { readonly kind: 'missed'; readonly sentence: string };

/**
 * Quick Recipe: a draft recipe and a lead picture from nothing but a name, saved together (the Workflows page's
 * second journey).
 *
 * **Nothing here is new server-side.** The draft is AIREC-002 asked with a one-field brief; the picture is the
 * Content Pipeline's own chain — plan a look (IMG-001), compose a prompt for its main shot (IMG-002), make one
 * picture (IMG-003) — run without the choices in between; the save is the draft's acceptance, the picture's
 * keep, and a `Hero` asset link. This page only does them in order so a creator does not have to.
 *
 * **The draft comes first, and the picture is planned from it.** Nothing is asked of the picture chain until
 * the draft has been written, and the look is then planned from the name plus what the draft lists as its
 * ingredients — so the picture shows the dish this draft makes rather than whatever the name suggests. A draft
 * that fails costs no picture.
 *
 * **One press saves, and nothing is saved before it.** The draft stays a proposal and the picture stays staged
 * until "Save" — the creator has both in front of them when they decide (.claude/rules/ai.md). What saving
 * creates is a Draft recipe, which they go on editing in the recipe editor.
 *
 * **The picture never holds the recipe up.** Making pictures can be switched off, refused or slow; each of those
 * is a sentence beside the draft, and the recipe saves without one.
 *
 * **The run is in the address.** Every request id is a query parameter, written with `replaceUrl`, so a reload
 * picks the run back up and asks for nothing twice. Nothing is kept on the device.
 *
 * **The draft reaches the picture as a brief, not as a recipe.** There is no recipe to pin until one is saved,
 * so the ingredients travel as the request's own brief beside the name — untrusted text, as generated words
 * should be (.claude/rules/ai.md). Nothing has compared the pixels with the draft, and the page says so: a
 * picture that shows something the draft does not contain is the creator's to catch (.claude/rules/media.md).
 */
@Component({
  selector: 'cp-quick-recipe',
  standalone: true,
  imports: [
    RouterLink,
    AiAllowanceNoticeComponent,
    AiOperationStatusComponent,
    ContentPipelineStagedImageComponent,
    CpBackLinkComponent,
    CpButtonComponent,
    CpCardComponent,
    CpFactComponent,
    CpFactsComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpGeneratedLabelComponent,
    CpLightboxComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpProgressComponent,
    CpSkeletonComponent,
    CpStepperComponent,
    CpTopicCardComponent,
  ],
  templateUrl: './quick-recipe.component.html',
  styleUrl: './quick-recipe.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuickRecipeComponent {
  protected readonly pageTitle = injectRouteTitle();

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly usage = inject(AiUsageService);
  private readonly drafts = inject(RecipeDraftService);
  private readonly confirmer = inject(ConfirmService);
  private readonly concepts = inject(PhotographyConceptService);
  private readonly prompts = inject(ImagePromptService);
  private readonly images = inject(GeneratedImageService);
  private readonly dam = inject(DamAssetService);
  private readonly recipes = inject(RecipeService);
  private readonly links = inject(RecipeAssetLinkService);

  protected readonly nameInputId = NAME_INPUT_ID;
  protected readonly headingId = HEADING_ID;
  protected readonly nameMaxLength = QUICK_RECIPE_NAME_MAX_LENGTH;
  protected readonly allowance = this.usage.allowance;

  readonly workspaceSlug = signal('');

  /** What the creator called the dish. Theirs, and sent exactly as written. */
  protected readonly name = signal('');
  protected readonly nameError = signal('');
  protected readonly announcement = signal('');

  protected readonly draft = new AiOperationTracker((id) => this.drafts.watchStatus(this.workspaceSlug(), id));
  private readonly looks = new AiOperationTracker((id) => this.concepts.watch(this.workspaceSlug(), id));
  private readonly prompt = new AiOperationTracker((id) => this.prompts.watch(this.workspaceSlug(), id));
  private readonly picture = new GeneratedImageTracker((id) => this.images.watch(this.workspaceSlug(), id));

  private readonly draftKey = new IdempotencyKey();
  private readonly looksKey = new IdempotencyKey();
  private readonly promptKey = new IdempotencyKey();
  private readonly pictureKey = new IdempotencyKey();
  private readonly keepKey = new IdempotencyKey();

  /** A look came back with nothing to compose from, or a prompt with no text. Said once, in plain words. */
  private readonly pictureProblem = signal<string | null>(null);

  protected readonly saving = signal(false);
  /** Why the recipe was not created, in words. */
  protected readonly saveProblem = signal<string | null>(null);
  /** The recipe this run created. Set the moment it exists, so nothing after it can create a second. */
  protected readonly savedRecipeId = signal<string | null>(null);
  /** Why the picture did not reach the saved recipe. The recipe itself is safe. */
  protected readonly pictureSaveProblem = signal<string | null>(null);

  // -------------------------------------------------------------------------
  // Who is asking
  // -------------------------------------------------------------------------

  private readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  /** An unknown workspace and one this creator is not in answer alike, so neither discloses the other. */
  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';

    const membership = this.membership();
    if (membership === null) return 'not_found';

    return membership.role === 'Viewer' ? 'read_only' : 'ready';
  });

  // -------------------------------------------------------------------------
  // Where the run is
  // -------------------------------------------------------------------------

  /**
   * The draft there is to read, or null.
   *
   * A draft already turned into a recipe still reads — a reload after saving lands here — and saving it again
   * answers with the recipe it became rather than a second one.
   */
  protected readonly reviewOperation = computed<AiProposalStatus | null>(() => {
    const phase = this.draft.phase();
    if (phase.kind === 'ready') return phase.operation;

    const operation = phase.kind === 'watching' ? phase.operation : null;

    return operation !== null && operation.proposal !== null && operation.status === 'Accepted' ? operation : null;
  });

  protected readonly alreadySaved = computed(() => this.reviewOperation()?.status === 'Accepted');

  protected readonly recipeDraft = computed<RecipeDraft>(() =>
    draftFromProposal(this.reviewOperation()?.proposal ?? null),
  );

  protected readonly step = computed<StepId>(() => {
    if (this.reviewOperation() !== null) return 'review';

    const kind = this.draft.phase().kind;

    return kind === 'submitting' || kind === 'watching' || kind === 'failed' || kind === 'gone' ? 'making' : 'name';
  });

  private readonly stepIndex = computed(() => STEPS.findIndex((entry) => entry.id === this.step()));
  protected readonly stepLabel = computed(() => STEPS[this.stepIndex()].label);
  protected readonly progressText = computed(() => `Step ${this.stepIndex() + 1} of ${STEPS.length}`);
  protected readonly percent = computed(() => Math.round(((this.stepIndex() + 1) / STEPS.length) * 100));

  protected readonly stepperSteps = computed<readonly CpStep[]>(() =>
    STEPS.map((entry, index) => ({
      id: entry.id,
      label: entry.label,
      status: index < this.stepIndex() ? 'done' : index === this.stepIndex() ? 'current' : 'upcoming',
    })),
  );

  /** The draft request as the status line reads it, while there is one to read. */
  protected readonly draftOperation = computed<AiProposalStatus | null>(() => {
    const phase = this.draft.phase();

    return phase.kind === 'watching' || phase.kind === 'failed' ? phase.operation : null;
  });

  protected readonly quotaRefusal = computed(() => {
    const phase = this.draft.phase();

    return phase.kind === 'blocked' ? phase.refusal : null;
  });

  /** Why the draft could not be asked for, as a sentence under the name. Null when nothing went wrong. */
  protected readonly askProblem = computed<string | null>(() => {
    const phase = this.draft.phase();

    switch (phase.kind) {
      case 'not_enabled':
        return 'Writing recipe drafts is switched off for this site, so nothing was asked for.';
      case 'forbidden':
        return 'You need Contributor access in this workspace to make a recipe.';
      case 'refused':
        return (
          [phase.message, ...Object.values(phase.fieldErrors).flat()].filter((line) => line !== '').join(' ') ||
          'That could not be asked for as it stands.'
        );
      case 'key_reused':
      case 'unavailable':
        return 'That could not be asked for just now. Nothing was made — try again.';
      default:
        return null;
    }
  });

  // -------------------------------------------------------------------------
  // The picture
  // -------------------------------------------------------------------------

  protected readonly pictureView = computed<PictureView>(() => {
    const problem = this.pictureProblem();
    if (problem !== null) return { kind: 'missed', sentence: problem };

    const picture = this.picture.phase();

    switch (picture.kind) {
      case 'submitting':
      case 'watching':
        return { kind: 'working', sentence: 'Making the picture…' };
      case 'ready': {
        const image = picture.operation.images.find((entry) => isStagedImageActionable(entry.status));

        return image
          ? { kind: 'ready', image }
          : { kind: 'missed', sentence: 'The picture that was made is not there any more.' };
      }
      case 'failed':
        return {
          kind: 'missed',
          sentence: generatedImageFailureSentence(picture.operation.failureCategory, picture.operation.failureSummary),
        };
      case 'refused':
        return { kind: 'missed', sentence: picture.message };
      case 'rate_limited':
        return { kind: 'missed', sentence: 'Too many requests at once, so no picture was made. Try it again in a moment.' };
      case 'forbidden':
        return { kind: 'missed', sentence: 'Your role in this workspace does not allow making pictures.' };
      case 'gone':
        return { kind: 'missed', sentence: 'The picture that was being made is not there any more.' };
      case 'unavailable':
        return { kind: 'missed', sentence: 'The picture could not be asked for just now.' };
      case 'idle':
        break;
    }

    return planningView(this.prompt.phase(), 'Writing the brief for the picture…')
      ?? planningView(this.looks.phase(), 'Planning the picture…')
      ?? { kind: 'none' };
  });

  /** The staged picture a save would keep, or null when there is none to keep. */
  private readonly stagedPicture = computed(() => {
    const view = this.pictureView();

    return view.kind === 'ready' ? view.image : null;
  });

  /** Whether the picture is open large. Only ever true while there is a picture to show. */
  protected readonly pictureOpen = signal(false);

  /**
   * What the large view says about the picture: its size, which the server stated. Never what it shows —
   * nothing here has looked at the pixels (.claude/rules/media.md).
   */
  protected readonly pictureFacts = computed(() => {
    const image = this.stagedPicture();

    return image === null ? '' : `${image.width} × ${image.height} pixels`;
  });

  protected readonly canSaveWithPicture = computed(() => this.stagedPicture() !== null);
  protected readonly pictureWorking = computed(() => this.pictureView().kind === 'working');

  /** Why this draft cannot become a recipe, or null. The one thing acceptance would refuse outright. */
  protected readonly saveBlocker = computed<string | null>(() => {
    const title = this.recipeDraft().title;

    return title === null || title.trim() === ''
      ? 'This draft came back without a title, and a recipe needs one. Start again to ask for another.'
      : null;
  });

  constructor() {
    void this.memberships.ensureLoaded();
    void this.usage.ensureLoaded();

    inject(DestroyRef).onDestroy(() => {
      this.draft.destroy();
      this.looks.destroy();
      this.prompt.destroy();
      this.picture.destroy();
    });

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
        if (slug === null) throw new Error('QuickRecipeComponent route is missing a workspaceSlug segment.');
        if (slug === this.workspaceSlug()) return;

        // Nothing of the previous workspace's run may outlive the move: its ids resolve nowhere else, and its
        // draft is not this workspace's to show (.claude/rules/tenancy.md).
        this.clearRun();
        this.workspaceSlug.set(slug);
        this.restoreFromUrl();
      });

    // The chain. Each link asks once the one before it has answered, which is also what carries a reloaded run
    // on from wherever it had got to. `idle` is the guard: a tracker that has been asked is never asked again.
    //
    // Its first link waits for the draft, because the picture is planned from what the draft contains. A draft
    // already saved asks for nothing: its picture, if it had one, was decided with it.
    effect(() => {
      const drafted = this.reviewOperation();
      if (drafted === null || drafted.status === 'Accepted' || this.savedRecipeId() !== null) return;
      if (this.phase() !== 'ready' || this.pictureProblem() !== null) return;
      if (
        this.looks.phase().kind !== 'idle' ||
        this.prompt.phase().kind !== 'idle' ||
        this.picture.phase().kind !== 'idle'
      ) {
        return;
      }

      untracked(() => void this.planPicture());
    });

    effect(() => {
      const looks = this.looks.phase();
      if (looks.kind !== 'ready' || this.prompt.phase().kind !== 'idle' || this.pictureProblem() !== null) return;

      untracked(() => void this.composePrompt(looks.operation));
    });

    effect(() => {
      const prompt = this.prompt.phase();
      if (prompt.kind !== 'ready' || this.picture.phase().kind !== 'idle' || this.pictureProblem() !== null) return;

      untracked(() => void this.makePicture(prompt.operation));
    });

    // A step change moves focus to the card's heading. Not on arrival: the page head has just been read.
    let previous: StepId | null = null;
    effect(() => {
      const step = this.step();
      if (previous !== null && previous !== step) {
        this.focusHeading();
        this.announcement.set(`${this.stepLabel()}. ${this.progressText()}.`);
      }
      previous = step;
    });
  }

  // -------------------------------------------------------------------------
  // Asking
  // -------------------------------------------------------------------------

  protected onNameInput(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
    this.nameError.set('');
  }

  /** Ask for the draft. The picture follows on its own once there is a draft to plan it from. */
  protected async start(event?: Event): Promise<void> {
    event?.preventDefault();
    if (this.draft.busy()) return;

    const name = this.name().trim();
    if (name === '') {
      this.nameError.set('Say what the dish is called.');
      this.focusName();
      return;
    }
    if (name.length > QUICK_RECIPE_NAME_MAX_LENGTH) {
      this.nameError.set(`Keep the name to ${QUICK_RECIPE_NAME_MAX_LENGTH} characters or fewer.`);
      this.focusName();
      return;
    }

    const slug = this.workspaceSlug();
    const requestId = await this.draft.submit(() =>
      this.drafts.requestDraftForDish(slug, name, this.draftKey.next()),
    );

    if (requestId === null) {
      // A reused key is the one refusal a fresh key fixes; every other failure keeps it so a retry replays.
      if (this.draft.phase().kind === 'key_reused') this.draftKey.clear();
      return;
    }

    this.draftKey.clear();
    this.writeUrl();
    void this.usage.refresh();
  }

  /** A deliberate second go at the draft after it failed: a new question, so a new key. */
  protected tryDraftAgain(): void {
    this.draftKey.clear();
    this.draft.reset();
    void this.start();
  }

  protected checkDraftAgain(): void {
    const requestId = this.draft.requestId();
    if (requestId !== null) this.draft.resume(requestId);
  }

  /**
   * Another picture for the same draft. A new question each time, so new keys throughout.
   *
   * Only clears the chain: the effect that starts it is what asks, so there is one place a picture is asked for
   * and no way to ask twice.
   */
  protected anotherPicture(): void {
    if (this.pictureWorking() || this.saving()) return;

    this.resetPicture();
    this.writeUrl();
  }

  /** Open the picture large. Focuses its button first, so closing returns there in every browser. */
  protected viewPictureLarger(event: Event): void {
    (event.currentTarget as HTMLElement).focus();
    this.pictureOpen.set(true);
  }

  private async planPicture(): Promise<void> {
    const name = this.name().trim();
    if (name === '') {
      this.pictureProblem.set('There is no name to make a picture from. Start again to make one.');
      return;
    }

    const requestId = await this.looks.submit(() =>
      this.concepts.request(
        this.workspaceSlug(),
        {
          channelKey: null,
          dishName: name,
          // What the draft is made of. The name says what the dish is called and the planner may read nothing
          // more out of it, so this is the only thing that tells the picture what is in the dish.
          creatorConcept: pictureBriefFromDraft(this.recipeDraft(), PHOTOGRAPHY_CREATOR_CONCEPT_MAX_LENGTH),
          sceneOverrides: [],
          styleOverrides: [],
        },
        this.looksKey.next(),
      ),
    );
    if (requestId === null) return;

    this.looksKey.clear();
    this.writeUrl();
  }

  private async composePrompt(planned: AiProposalStatus): Promise<void> {
    // The first look that can be named, and its main shot where it planned one: this flow makes one lead
    // picture, so the choice a creator makes in the Content Pipeline has only one sensible answer here.
    const concept = decodePhotographyConcepts(planned.proposal).find(
      (candidate) => candidate.conceptId !== '' && candidate.shots.length > 0,
    );
    if (concept === undefined) {
      this.pictureProblem.set('No look came back to make a picture from.');
      return;
    }

    const shotKind: PhotographyShotKind = concept.shots.some((shot) => shot.kind === 'Hero')
      ? 'Hero'
      : concept.shots[0].kind;
    const name = this.name().trim();

    const requestId = await this.prompt.submit(() =>
      this.prompts.request(
        this.workspaceSlug(),
        {
          conceptRequestId: planned.aiProposalRequestId,
          conceptId: concept.conceptId,
          shotKind,
          channelKey: null,
          dishName: name === '' ? null : name,
          briefDocumentId: null,
          sceneOverrides: [],
          styleOverrides: [],
        },
        this.promptKey.next(),
      ),
    );
    if (requestId === null) return;

    this.promptKey.clear();
    this.writeUrl();
  }

  private async makePicture(composed: AiProposalStatus): Promise<void> {
    const prompt = decodeComposedImagePrompt(composed.proposal);
    if (prompt === null || prompt.prompt.trim() === '') {
      this.pictureProblem.set('No brief came back to make a picture from.');
      return;
    }

    const operationId = await this.picture.submit(() =>
      this.images.request(
        this.workspaceSlug(),
        {
          promptText: prompt.prompt.slice(0, GENERATED_IMAGE_PROMPT_MAX_LENGTH),
          avoidText: avoidTextFor(prompt.avoid),
          variantCount: 1,
        },
        this.pictureKey.next(),
      ),
    );
    if (operationId === null) return;

    this.pictureKey.clear();
    this.writeUrl();
    void this.usage.refresh();
  }

  // -------------------------------------------------------------------------
  // Saving
  // -------------------------------------------------------------------------

  /**
   * Save the recipe, and with it the picture when there is one and it was asked for.
   *
   * Three writes in order — the recipe, the picture into the library, the picture onto the recipe — and each
   * is safe to repeat: accepting a draft twice answers with the same recipe, keeping a picture twice with the
   * same asset, and a recipe that already has a lead picture is left as it is.
   */
  protected async save(withPicture: boolean): Promise<void> {
    const operation = this.reviewOperation();
    const proposal = operation?.proposal ?? null;
    if (operation === null || proposal === null || this.saving() || this.saveBlocker() !== null) return;

    const slug = this.workspaceSlug();
    const image = withPicture ? this.stagedPicture() : null;

    this.saving.set(true);
    this.saveProblem.set(null);
    this.pictureSaveProblem.set(null);

    try {
      let recipeId = this.savedRecipeId();

      if (recipeId === null) {
        const accepted = await this.drafts.acceptDraft(slug, operation.aiProposalRequestId, {
          acceptedChangeIds: proposal.changes.map((change) => change.changeId),
          rewrites: [],
        });

        if (accepted.status !== 'accepted') {
          this.saveProblem.set(ACCEPT_PROBLEMS[accepted.status] ?? (accepted.status === 'refused' ? accepted.message : ''));
          return;
        }

        recipeId = accepted.recipeId;
        this.savedRecipeId.set(recipeId);
      }

      if (image !== null) {
        const problem = await this.attachPicture(slug, recipeId, image.id);
        if (problem !== null) {
          this.pictureSaveProblem.set(problem);
          this.announcement.set('Your recipe is saved. The picture was not added to it.');
          return;
        }
      }

      await this.router.navigate(['/', slug, 'recipes', recipeId]);
    } finally {
      this.saving.set(false);
    }
  }

  /** Keeps the staged picture and makes it the recipe's lead picture. Null when it is there; otherwise why not. */
  private async attachPicture(slug: string, recipeId: string, generatedImageId: string): Promise<string | null> {
    const title = (this.recipeDraft().title ?? this.name()).trim().slice(0, DAM_ASSET_LIMITS.titleMaxLength);

    // No alt text: nothing here has looked at the pixels, and describing them from the recipe would claim what
    // nobody has seen (.claude/rules/media.md). The creator writes it on the picture's own page.
    const kept = await this.dam.keepGeneratedImage(
      slug,
      encodeDamKeep(generatedImageId, { title, altText: '', tagIds: [], recipe: null, keepPrompt: false }, null),
      this.keepKey.next(),
    );
    if (kept.status !== 'saved') {
      return kept.status === 'image_gone'
        ? 'The picture is not there to keep any more.'
        : 'The picture could not be put in your library just now.';
    }
    this.keepKey.clear();

    const read = await this.recipes.getRecipeDetail(slug, recipeId);
    if (read.status !== 'found') return IN_LIBRARY_NOT_LINKED;
    // A lead picture is already there — this is a repeat of a save that got further than its answer did.
    if (read.recipe.assetLinks.some((link) => link.role === 'Hero')) return null;

    const linked = await this.links.link(
      slug,
      recipeId,
      {
        mediaAssetId: kept.asset.id,
        role: 'Hero',
        instructionStepId: null,
        versionNumber: null,
        caption: null,
        expectedConcurrencyToken: read.recipe.concurrencyToken,
      },
      // Fresh each time: the request quotes a token that changes on every re-read, and a key cannot be reused
      // for a different request. The check above is what stops a second link.
      crypto.randomUUID(),
    );

    return linked.status === 'linked' || linked.status === 'hero_exists' || linked.status === 'already_linked'
      ? null
      : IN_LIBRARY_NOT_LINKED;
  }

  protected recipeLink(recipeId: string): readonly string[] {
    return ['/', this.workspaceSlug(), 'recipes', recipeId];
  }

  /** The line-by-line review of this same draft, for a creator who wants to change it before it is a recipe. */
  protected readonly reviewLink = computed(() => ['/', this.workspaceSlug(), 'ai-recipe-studio', 'draft']);
  protected readonly reviewQuery = computed(() => ({ request: this.draft.requestId() }));
  protected readonly workflowsLink = computed(() => ['/', this.workspaceSlug(), 'workflows']);

  // -------------------------------------------------------------------------
  // Starting over, and the address
  // -------------------------------------------------------------------------

  /**
   * Back to the name. Nothing is cancelled, because no route cancels: what was asked for is left to finish and
   * expire on its own, and this page stops watching it.
   *
   * <p>Asks first while there is a draft the creator has not saved. The draft itself is not deleted — it is
   * still the server's, and still reachable by its request — but this page is the only place holding that
   * request, so clearing it is the creator's last way back to the draft and its picture. Saved work is
   * untouched, and a page with nothing to lose does not ask.</p>
   */
  protected async startOver(): Promise<void> {
    if (this.draft.requestId() !== null && this.savedRecipeId() === null) {
      const agreed = await this.confirmer.confirm({
        title: 'Start again with another name?',
        message:
          'This draft and its picture are not saved yet, and starting again is your last way back to them. ' +
          'Anything you have already saved is untouched.',
        confirmLabel: 'Start again',
        cancelLabel: 'Keep this draft',
        tone: 'neutral',
      });
      if (!agreed) return;
    }

    this.clearRun();
    this.writeUrl();
    this.focusName();
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }

  private clearRun(): void {
    this.draft.reset();
    this.draftKey.clear();
    this.resetPicture();
    this.name.set('');
    this.nameError.set('');
    this.saveProblem.set(null);
    this.pictureSaveProblem.set(null);
    this.savedRecipeId.set(null);
  }

  private resetPicture(): void {
    this.looks.reset();
    this.prompt.reset();
    this.picture.reset();
    this.looksKey.clear();
    this.promptKey.clear();
    this.pictureKey.clear();
    this.keepKey.clear();
    this.pictureProblem.set(null);
    this.pictureOpen.set(false);
  }

  private restoreFromUrl(): void {
    const query = this.route.snapshot.queryParamMap;
    const draftId = query.get('draft');
    if (!draftId) return;

    this.name.set(query.get('name') ?? '');
    this.draft.resume(draftId);

    // Only the furthest link is picked back up; the effects in the constructor carry it on from there.
    const pictureId = query.get('picture');
    const promptId = query.get('prompt');
    const lookId = query.get('look');

    if (pictureId) this.picture.resume(pictureId);
    else if (promptId) this.prompt.resume(promptId);
    else if (lookId) this.looks.resume(lookId);
  }

  private writeUrl(): void {
    const draftId = this.draft.requestId();

    void this.router.navigate([], {
      relativeTo: this.route,
      replaceUrl: true,
      queryParams:
        draftId === null
          ? {}
          : {
              name: this.name().trim(),
              draft: draftId,
              look: this.looks.requestId(),
              prompt: this.prompt.requestId(),
              picture: this.picture.operationId(),
            },
    });
  }

  private focusHeading(): void {
    afterNextRender(() => document.getElementById(HEADING_ID)?.focus(), { injector: this.injector });
  }

  private focusName(): void {
    afterNextRender(() => document.getElementById(NAME_INPUT_ID)?.focus(), { injector: this.injector });
  }

  /** Every route node from this one up to the root, so the slug can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}

const IN_LIBRARY_NOT_LINKED =
  'The picture is in your library, but it could not be set as the lead picture on the recipe just now.';

/** Why accepting the draft did not create a recipe, for every answer but the server's own refusal. */
const ACCEPT_PROBLEMS: Readonly<Record<string, string>> = {
  already_decided: 'This draft has already been decided another way, so no recipe was created from it. Start again to make one.',
  not_found: 'This draft is not there any more, so no recipe was created. Start again to make one.',
  forbidden: 'Your role in this workspace does not permit creating recipes.',
  unavailable:
    "Couldn't reach the server, so we don't know whether the recipe was created. Save again — if it was, you will be taken to that recipe rather than getting a second one.",
};

/** One of the two planning requests as the picture's state, or null when it has not been asked. */
function planningView(phase: AiOperationPhase, working: string): PictureView | null {
  switch (phase.kind) {
    case 'idle':
      return null;
    case 'submitting':
    case 'watching':
    // Answered, and the next link is about to be asked: still on its way as far as a creator can tell.
    case 'ready':
      return { kind: 'working', sentence: working };
    case 'not_enabled':
      return { kind: 'missed', sentence: 'Planning pictures is switched off for this site, so no picture was made.' };
    case 'blocked':
      return { kind: 'missed', sentence: "Your AI allowance won't cover a picture right now, so none was made." };
    case 'forbidden':
      return { kind: 'missed', sentence: 'Your role in this workspace does not allow making pictures.' };
    case 'failed':
    case 'gone':
      return { kind: 'missed', sentence: 'The picture could not be planned this time.' };
    default:
      return { kind: 'missed', sentence: 'The picture could not be asked for just now.' };
  }
}
