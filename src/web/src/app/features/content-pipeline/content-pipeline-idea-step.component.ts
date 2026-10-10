import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_SEED_FACET_LABELS,
  CONTENT_SEED_KEEP_NAMES,
  CONTENT_PIPELINE_BRIEF_SOURCE_LABELS,
  ContentPipelineBriefSource,
  ContentPipelineDraft,
  ContentPipelineKeep,
  ContentSeedKeepName,
  contentPipelineBriefFor,
  isContentPipelineBriefEdited,
  contentSeedQueryFor,
  contentSubjectOf,
  linkedRecipeKey,
} from '../../models/content-pipeline.models';
import {
  CONTENT_SEED_FIELD_NAMES,
  CONTENT_SEED_RECIPE_FIELD_NAME,
  CONTENT_SEED_TOKEN_MAX_LENGTH,
  DAYS_OF_WEEK,
  DayOfWeek,
  ContentSeed,
  ContentSeedFacet,
  contentSeedFieldError,
  isContentSeedToken,
} from '../../models/content-seed.models';
import { LinkedRecipe } from '../../models/creative-context.models';
import { DishFacetsReading, decodeDishFacetsReading } from '../../models/dish-facet.models';
import { CookingTechnique, ReferenceEntry } from '../../models/reference.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ContentSeedService } from '../../services/content-seed.service';
import { DishFacetService } from '../../services/dish-facet.service';
import { ReferenceService } from '../../services/reference.service';
import { AiOperationTracker } from '../ai/ai-operation-tracker';
import { IdempotencyKey } from './content-pipeline-idempotency';

/** What each choice means, in a line. The brief itself is shown beneath as the example. */
function briefChoiceHints(where: string): Readonly<Record<ContentPipelineBriefSource, string>> {
  return {
    Description: `Only what you wrote ${where}.`,
    Idea: 'Only the idea you picked. Your description is kept, and not used.',
    Combined: 'Your description as you wrote it, with the idea underneath.',
  };
}

/** Where the step has got to. An idea on screen is a suggestion; only `accepted` on the draft is a decision. */
type IdeaState =
  | { readonly status: 'idle' }
  | { readonly status: 'generating' }
  | { readonly status: 'shown'; readonly seed: ContentSeed }
  | { readonly status: 'refused'; readonly fieldErrors: Record<string, readonly string[]> }
  | { readonly status: 'unavailable' };

/** One line of an idea as it is shown: what it is, what it says, and whether the creator asked for it. */
interface FacetRow {
  readonly name: ContentSeedKeepName | 'channel' | 'day' | 'theme';
  readonly label: string;
  readonly displayName: string;
  /** What this part is set to, as the catalogue keys it. What its picker shows as chosen. */
  readonly key: string;
  /**
   * Everything this part could be changed to, or empty where it cannot be changed here: the theme, a part
   * taken from the linked recipe, and a part whose catalogue could not be read.
   */
  readonly options: readonly FacetOption[];
  /** The server's word: true when the creator asked for this value rather than the seed choosing it. */
  readonly pinned: boolean;
  /**
   * False for the theme, which follows the day rather than being chosen, and for a part taken from the linked
   * recipe, which is the recipe's to say and not something a re-roll would change.
   */
  readonly keepable: boolean;
  /** True when this is the linked recipe's own cuisine, course or method. */
  readonly fromRecipe: boolean;
}

interface FacetOption {
  readonly value: string;
  readonly label: string;
}

type RefusableName = ContentSeedKeepName | 'channel' | 'day';

/**
 * The three catalogues only the idea's own rows need: nothing else on this step offers a photo style, an
 * occasion or a channel.
 *
 * Each is null until read and null if it could not be. They fail apart, and quietly: a row without its list
 * is shown as it always was, with Keep, so one unreachable catalogue costs one picker and no sentence.
 */
interface RowCatalogues {
  readonly photographyStyle: readonly FacetOption[] | null;
  readonly occasion: readonly FacetOption[] | null;
  readonly channel: readonly FacetOption[] | null;
}

const DAY_OPTIONS: readonly FacetOption[] = DAYS_OF_WEEK.map((day) => ({ value: day, label: day }));

/**
 * The three facets a creator can state outright, before anything has been suggested.
 *
 * They are the ones that say what the dish *is*, which is why these three and not the other two: a photo
 * style and an occasion are decisions about the post, and the generator's suggestion for either is a fine
 * place to start. A cuisine, a dish type and a method are facts about the food, and a creator who already
 * knows them has nothing to gain from being handed a draw.
 */
type StatableFacet = 'cuisine' | 'dishType' | 'method';

const STATABLE_FACETS: readonly StatableFacet[] = ['cuisine', 'dishType', 'method'];

/**
 * Where the three vocabularies have got to.
 *
 * One state for all three rather than one each. They are three reads of the same catalogue API, so a failure
 * is almost always a failure of all of them, and three separate sentences about it would be three ways of
 * saying the vocabulary is unreachable.
 */
type VocabularyState =
  | { readonly status: 'loading' }
  | {
      readonly status: 'ready';
      readonly cuisines: readonly ReferenceEntry[];
      readonly courses: readonly ReferenceEntry[];
      readonly techniques: readonly CookingTechnique[];
    }
  | { readonly status: 'unavailable' };

interface RefusedPin {
  readonly name: RefusableName;
  readonly label: string;
  readonly message: string;
}

/** The server's message for a refused pin, read back under our own name for it. */
function errorFor(
  fieldErrors: Record<string, readonly string[]>,
  name: keyof typeof CONTENT_SEED_FIELD_NAMES,
): string {
  return contentSeedFieldError(fieldErrors, CONTENT_SEED_FIELD_NAMES[name]);
}

/**
 * What a picked idea that is no longer about what this run is about says of itself.
 *
 * The first three are about the linked recipe; `subject_changed` is the same situation one step down — the run
 * has no recipe and the creator has renamed what the picture is of since they picked.
 */
type SubjectMismatch = 'picked_before_linking' | 'other_recipe' | 'recipe_unlinked' | 'subject_changed';

/**
 * Step 2 of the Content Pipeline: an idea to start from, and the creator's decision about it (PIPE-UI-002).
 *
 * **An idea is a suggestion until it is picked.** The generator writes nothing, calls no model, and has no
 * record to fetch later — so nothing is kept while an idea is merely on screen except the code that reproduces
 * it. Picking one is the moment it becomes part of the run.
 *
 * **Each part can be changed on its own, or kept while the rest is tried again.** Every part of an idea carries
 * a picker over its own catalogue, and choosing from it is the same act as stating that part: it is written
 * where a Keep would write it, and the idea is asked for again under the same code so only that part moves.
 * Two of them — the channel and the day — are answers that already have a home on the previous step, so
 * changing or keeping one writes it there instead of storing it twice. The theme follows the day and a linked
 * recipe's own facts are the recipe's, so neither is offered for changing.
 *
 * **What the creator already knows is stated, not drawn for.** The cuisine, dish type and method can be set
 * outright, before anything has been suggested, and the token then only chooses what was left open. Without
 * this the only way to reach a particular cuisine was to re-roll until it came up — and because each facet is
 * drawn independently (`ContentSeedSelector`), reaching a plausible three at once meant re-rolling past every
 * combination that was not. Those three and not the other five: see {@link StatableFacet}. The pins travel in
 * the same `keep` map the Keep buttons write, so a stated facet and a kept one are the same thing and the two
 * controls cannot disagree.
 *
 * **Nothing sets these three silently.** Whatever puts a value in one of them — the creator, a Keep button, or
 * a model's suggestion — is visible on screen and can be changed before any idea is asked for. The convention
 * it keeps is stated on the server (`IContentSeedBusiness`, and the dish-name docstrings on the AI request
 * models) and put to the model by `recipe.concepts`: a dish name says what a dish is called, so nothing may
 * turn it into a *fact* about cuisine, course or method behind the creator's back. A proposal they can see and
 * overrule is not that; a quiet derivation would be.
 *
 * **And this step is where that promise is actually kept.** The server will read a name into all three facets
 * and has nothing to stop a reading reaching one the creator has answered — see `AiDishFacetInputs`. Applying
 * a suggestion only to a control they have not touched is this component's obligation, not the API's.
 *
 * **The typed name is read once, on arrival.** With no recipe linked and no idea picked, the name from the
 * first step is sent to be read and each facet it names fills the control that is still on "Suggest one",
 * with the reading's own reason beside it. A reading fills a control once: its request id and the name it was
 * for are kept on the draft, so a return visit reads the same answer back instead of buying another, and a
 * control the creator has since emptied stays empty. A reading that fails says so quietly and leaves the three
 * controls exactly as usable as they were.
 *
 * **A refused pin is shown, never dropped.** The server refuses a key no catalogue has, or one naming a retired
 * entry, and says which. Generating around it instead would hand the creator an idea that quietly ignored what
 * they asked for.
 *
 * **The safety caution is rendered whenever the method carries one**, and `false` is rendered as nothing at all:
 * it means no caution has been attached, never that a technique is safe (.claude/rules/ai.md).
 *
 * **The idea never rewrites the creator's own concept.** Copying its wording across is a separate action, and
 * one that asks first when there is something there to lose (.claude/rules/recipes.md).
 *
 * **A linked recipe is what the idea is about.** Its cuisine, course and method are the recipe's own, shown as
 * such and not offered for keeping, and the idea names the recipe. A suggestion follows the link when it changes;
 * a *picked* idea is a decision, so it is left as it is and said to be out of step, with a way to ask again.
 *
 * **With no recipe, the name typed on the first step is what it is about instead.** The idea names it and
 * nothing else follows from it — a name says nothing about how a dish is cooked, so the cuisine, dish type and
 * method stay the generator's suggestions and are still offered for keeping. Renaming the subject moves a
 * suggestion and leaves a picked idea alone, exactly as relinking a recipe does.
 */
@Component({
  selector: 'cp-content-pipeline-idea-step',
  standalone: true,
  imports: [
    FormsModule,
    CpBadgeComponent,
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
  ],
  templateUrl: './content-pipeline-idea-step.component.html',
  styleUrl: './content-pipeline-idea-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineIdeaStepComponent implements OnInit {
  private readonly seeds = inject(ContentSeedService);
  private readonly dishFacets = inject(DishFacetService);
  private readonly brandProfiles = inject(BrandProfileService);
  private readonly reference = inject(ReferenceService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  /** The recipe linked to this run, with its pinned version, or null. The idea is built around it. */
  readonly recipe = input<LinkedRecipe | null>(null);
  /**
   * False where a day means nothing. The Image Studio offers an idea too, but it plans no week, so the day
   * and its theme are not shown there as parts to keep or change.
   */
  readonly showDay = input(true);
  /**
   * False where the name is typed on the same screen as this step. Reading on arrival would then mean reading
   * on every keystroke, each one a charged request — so there the creator asks for the reading themselves.
   */
  readonly readsNameOnArrival = input(true);
  /** Where the creator described their picture, as this surface says it. The studio has no first step. */
  readonly descriptionWhere = input('on the first step');
  readonly changed = output<ContentPipelineDraft>();
  /** One sentence for the shell's polite live region, so a change of idea is announced once. */
  readonly announced = output<string>();

  protected readonly tokenMaxLength = CONTENT_SEED_TOKEN_MAX_LENGTH;

  /** The creator's own description of the picture, from the setup step. Shown, never changed, here. */
  protected readonly description = computed(() => this.draft().config.concept);

  /** The choice is offered once there are two things to choose between: a description, and a picked idea. */
  protected readonly offersBriefChoice = computed(
    () => this.draft().seed.accepted !== null && this.description().trim() !== '',
  );

  /**
   * The three ways to plan the picture, each showing exactly the brief it would make.
   *
   * The text is the example so the creator reads what will be worked from before choosing, rather than
   * choosing a label and finding out on the next step.
   */
  protected readonly briefChoices = computed<readonly CpChoiceOption[]>(() => {
    const idea = this.draft().seed.accepted?.description ?? null;
    const description = this.description();

    return (['Description', 'Idea', 'Combined'] as const).map((source) => ({
      value: source,
      label: CONTENT_PIPELINE_BRIEF_SOURCE_LABELS[source],
      hint: briefChoiceHints(this.descriptionWhere())[source],
      example: contentPipelineBriefFor(source, description, idea) ?? undefined,
    }));
  });

  /**
   * Raised when a choice is declined, so the group is handed its value afresh.
   *
   * The group marks a tile the moment it is clicked. When the creator then keeps their edited brief, the
   * choice on the run has not changed — so without this nothing would tell the group to put its mark back,
   * and the screen would show a choice that was never made.
   */
  private readonly declined = signal(0);

  protected readonly briefChoice = computed<readonly string[]>(() => {
    this.declined();
    const source = this.draft().config.briefSource;

    return source === null ? [] : [source];
  });

  /** True when the brief on the run is no longer simply what its choice makes. */
  protected readonly briefEdited = computed(() => isContentPipelineBriefEdited(this.draft()));
  protected readonly state = signal<IdeaState>({ status: 'idle' });
  /** A code the creator typed or pasted, to come back to an idea they or someone else already had. */
  protected readonly tokenEntry = signal('');

  protected readonly vocabulary = signal<VocabularyState>({ status: 'loading' });
  protected readonly statableFacets = STATABLE_FACETS;
  private readonly rowCatalogues = signal<RowCatalogues>({ photographyStyle: null, occasion: null, channel: null });
  protected readonly facetLabels = CONTENT_SEED_FACET_LABELS;

  /**
   * Whether the creator can state a facet outright on this step.
   *
   * Only with no recipe linked. A linked recipe's own cuisine, course and technique are the canonical facts
   * about it and the server lets them win over a pin — so offering a picker beside them would be offering a
   * control that the next request ignores (`IContentSeedBusiness.ResolveAround`). With a recipe linked, the
   * facets panel's "From your recipe" badge is already the honest account of where they came from.
   */
  protected readonly offersStatedFacets = computed(() => this.recipe() === null && this.accepted() === null);

  /** The code stated for each facet, or '' for none. What the three selects show. */
  protected readonly stated = computed<Readonly<Record<StatableFacet, string>>>(() => {
    const keep = this.draft().seed.keep;

    return { cuisine: keep.cuisine ?? '', dishType: keep.dishType ?? '', method: keep.method ?? '' };
  });

  /** The vocabulary each select offers, in the catalogue's own order. Empty until the read lands. */
  protected readonly facetOptions = computed<Readonly<Record<StatableFacet, readonly ReferenceEntry[]>>>(() => {
    const state = this.vocabulary();
    if (state.status !== 'ready') return { cuisine: [], dishType: [], method: [] };

    return { cuisine: state.cuisines, dishType: state.courses, method: state.techniques };
  });

  /**
   * Every facet the creator has stated, as one string.
   *
   * A computed rather than a read inside the effect below, so that effect depends on the pins themselves and
   * not on every keystroke anywhere else in the draft.
   */
  private readonly keepKey = computed(() => {
    const { seed, config } = this.draft();

    // The channel and the day are pins too; they only live on the setup step's answers rather than in `keep`.
    return [...CONTENT_SEED_KEEP_NAMES.map((name) => seed.keep[name] ?? ''), config.channelKey ?? '', config.day ?? ''].join('|');
  });

  private request: Subscription | null = null;

  /** The reading of the typed name. Its own tracker, so it never shares a phase with the idea's request. */
  protected readonly nameTracker = new AiOperationTracker((requestId) =>
    this.dishFacets.watch(this.workspaceSlug(), requestId),
  );

  private readonly nameKey = new IdempotencyKey();

  /**
   * The name this component has already asked about, so one visit asks once.
   *
   * A reading that failed is not asked for again by the effect below — only by the creator, through
   * {@link readNameAgain} — or a route that is down would be asked on every change to the draft.
   */
  private nameAskedFor: string | null = null;

  /**
   * True while an idea the creator asked for is waiting on the name being read first.
   *
   * Where the name is not read on arrival, asking for an idea is the moment it is: an idea drawn before the
   * reading would pick a cuisine at random for a dish whose name already says one.
   */
  private readonly ideaAwaitsName = signal(false);

  /** The reading on screen, or null until there is one — and null for a reading of an earlier name. */
  protected readonly nameReading = computed<DishFacetsReading | null>(() => {
    const kept = this.draft().seed.nameReading ?? null;
    if (kept === null || kept.subject !== this.subject() || kept.requestId !== this.nameTracker.requestId()) {
      return null;
    }

    return decodeDishFacetsReading(this.nameTracker.proposal());
  });

  /**
   * What each control says about where its value came from.
   *
   * Said only while it is true: a facet the creator has changed since is theirs and carries no note, and a
   * declined facet explains itself only while the control is still empty.
   */
  protected readonly facetHints = computed<Readonly<Record<StatableFacet, string>>>(() => {
    const reading = this.nameReading();
    const stated = this.stated();
    const hint = (name: StatableFacet): string => {
      const facet = reading?.facets[name];
      if (facet === undefined) return '';

      const reason = facet.rationale === null ? '' : ` ${facet.rationale}`;
      if (facet.code === null) return stated[name] === '' ? `The name did not say.${reason}` : '';
      if (facet.code !== stated[name]) return '';

      const lead = facet.confidence === 'Possible' ? 'A guess from the name' : 'Suggested from the name';

      return `${lead} — change it if it is wrong.${reason}`;
    };

    return { cuisine: hint('cuisine'), dishType: hint('dishType'), method: hint('method') };
  });

  /** True where reading the name is the creator's to ask for, and there is a name not yet read to ask about. */
  protected readonly canReadName = computed(() => {
    const subject = this.subject();
    const kept = this.draft().seed.nameReading ?? null;

    return (
      !this.readsNameOnArrival() &&
      this.offersStatedFacets() &&
      subject !== null &&
      (kept === null || kept.subject !== subject) &&
      !this.nameTracker.busy() &&
      !this.nameUnread()
    );
  });

  /** What the reading said about itself as a whole. */
  protected readonly nameWarnings = computed(() => this.nameReading()?.warnings ?? []);

  protected readonly readingName = computed(() => this.offersStatedFacets() && this.nameTracker.busy());

  /**
   * True when the name could not be read. A task switched off for this deployment is not that: nothing was
   * attempted, and there is nothing for a creator to try again.
   */
  protected readonly nameUnread = computed(() => {
    const kind = this.nameTracker.phase().kind;

    return (
      this.offersStatedFacets() &&
      this.subject() !== null &&
      kind !== 'idle' &&
      kind !== 'submitting' &&
      kind !== 'watching' &&
      kind !== 'ready' &&
      kind !== 'not_enabled'
    );
  });

  protected readonly accepted = computed(() => this.draft().seed.accepted);

  /** The idea on screen: the picked one if there is one, otherwise whatever was last generated. */
  protected readonly seed = computed<ContentSeed | null>(() => {
    const accepted = this.accepted();
    if (accepted !== null) return accepted;
    const state = this.state();

    return state.status === 'shown' ? state.seed : null;
  });

  /** The recipe the idea on screen was built around, by name, or null when it was built around none. */
  protected readonly builtAround = computed(() => this.seed()?.recipe?.title ?? null);

  /** The name the idea on screen was built around, where it was a name rather than a recipe. Null otherwise. */
  protected readonly builtAroundSubject = computed(() => this.seed()?.subject ?? null);

  /**
   * The typed name this run is about now, or null when a linked recipe is.
   *
   * The same decision every request makes, so what the screen says and what the next request asks for cannot
   * disagree.
   */
  private readonly subject = computed(() => contentSubjectOf(this.draft().config, this.recipe()));

  /**
   * How a *picked* idea is out of step with the recipe linked now, or null when it is not.
   *
   * Only ever said about a picked idea: a suggestion is simply asked for again.
   */
  protected readonly recipeMismatch = computed<SubjectMismatch | null>(() => {
    const accepted = this.accepted();
    if (accepted === null) return null;

    if (linkedRecipeKey(accepted.recipe) !== linkedRecipeKey(this.recipe())) {
      if (this.recipe() === null) return 'recipe_unlinked';

      return accepted.recipe === null ? 'picked_before_linking' : 'other_recipe';
    }

    // No recipe on either side, so the question is the name. Said only when both the idea and the run have
    // one: a name added after picking is not the idea being wrong about anything.
    const was = accepted.subject;
    const now = this.subject();

    return was !== null && now !== null && was !== now ? 'subject_changed' : null;
  });

  /** True when the server could not read the linked recipe, so no idea was made. */
  protected readonly recipeRefused = computed(() => {
    const state = this.state();

    return state.status === 'refused' && contentSeedFieldError(state.fieldErrors, CONTENT_SEED_RECIPE_FIELD_NAME) !== '';
  });

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.request?.unsubscribe();
      this.nameTracker.destroy();
    });

    // Read the typed name, or read back the reading already kept for it.
    effect(() => {
      const subject = this.subject();
      if (!this.offersStatedFacets() || subject === null) return;
      const kept = this.draft().seed.nameReading ?? null;

      untracked(() => {
        if (kept !== null && kept.subject === subject) {
          if (this.nameTracker.requestId() !== kept.requestId) this.nameTracker.resume(kept.requestId);
          return;
        }

        if (this.readsNameOnArrival() && this.nameAskedFor !== subject) void this.readName(subject);
      });
    });

    // An idea asked for before the name was read is asked for once the reading has been offered to the
    // controls — or has not arrived, in which case the idea is made without it rather than not at all.
    effect(() => {
      if (!this.ideaAwaitsName()) return;

      const kept = this.draft().seed.nameReading ?? null;
      const offered = kept !== null && kept.applied && kept.subject === this.subject();
      const kind = this.nameTracker.phase().kind;
      const waiting = kind === 'submitting' || kind === 'watching' || kind === 'ready';
      if (!offered && waiting && !this.nameTracker.stopped()) return;

      untracked(() => {
        this.ideaAwaitsName.set(false);
        this.run(null);
      });
    });

    // Offer a finished reading to the three controls, once, and only to one still on "Suggest one".
    effect(() => {
      const reading = this.nameReading();
      const kept = this.draft().seed.nameReading ?? null;
      if (reading === null || kept === null || kept.applied || !this.offersStatedFacets()) return;

      untracked(() => {
        const draft = this.draft();
        const keep = { ...draft.seed.keep };
        let filled = 0;
        for (const name of STATABLE_FACETS) {
          const code = reading.facets[name]?.code ?? null;
          if (code === null || keep[name] !== undefined) continue;
          keep[name] = code;
          filled += 1;
        }

        this.changed.emit({ ...draft, seed: { ...draft.seed, keep, nameReading: { ...kept, applied: true } } });
        this.announced.emit(
          filled === 0
            ? 'The name was read, and nothing was filled in from it.'
            : `Filled in ${filled} of ${STATABLE_FACETS.length} from the name. Change any that are wrong.`,
        );
      });
    });

    // A suggestion follows what the run is about — the linked recipe, or the name typed in its place — and what
    // the creator has stated about it: the same code, asked for again as things now stand. Re-asking with the
    // same token is what makes a newly stated cuisine change the cuisine and leave the rest of the idea alone;
    // a fresh token would re-roll everything. A picked idea is not touched here; that is a decision, and
    // `recipeMismatch` says it is out of step.
    effect(() => {
      const key = `${linkedRecipeKey(this.recipe())}|${this.subject() ?? ''}`;
      this.keepKey();

      untracked(() => {
        const state = this.state();
        if (state.status !== 'shown' || this.accepted() !== null) return;

        const seed = state.seed;
        const about = `${linkedRecipeKey(seed.recipe)}|${seed.subject ?? ''}`;
        if (about !== key || !ContentPipelineIdeaStepComponent.honoursKeep(seed, this.draft())) {
          this.run(seed.token);
        }
      });
    });
  }

  ngOnInit(): void {
    void this.loadVocabulary();
    void this.loadRowCatalogues();

    // A picked idea needs nothing fetched; it is stored whole. An unpicked one is reproduced from the code the
    // draft kept, which is what the contract offers in place of storing a suggestion.
    const { accepted, lastToken } = this.draft().seed;
    if (accepted === null && lastToken !== null) this.run(lastToken);
  }

  /**
   * True when every facet the creator stated is what the idea on screen actually shows.
   *
   * Compared against the seed rather than tracked separately, which is what makes releasing a pin cost
   * nothing: with no pin there is nothing to disagree with, so the idea on screen stands. A pin that happens
   * to match what was drawn is satisfied too, so pressing Keep on a facet never re-asks for the same idea.
   */
  private static honoursKeep(seed: ContentSeed, draft: ContentPipelineDraft): boolean {
    const keep: ContentPipelineKeep = draft.seed.keep;
    const { channelKey, day } = draft.config;
    if (channelKey !== null && channelKey !== seed.channel?.key) return false;
    if (day !== null && day !== seed.day.day) return false;

    const facets: Readonly<Record<ContentSeedKeepName, ContentSeedFacet | null>> = {
      cuisine: seed.cuisine,
      dishType: seed.dishType,
      method: seed.method,
      photographyStyle: seed.photographyStyle,
      occasion: seed.occasion,
    };

    return CONTENT_SEED_KEEP_NAMES.every((name) => {
      const stated = keep[name];

      return stated === undefined || stated === facets[name]?.key;
    });
  }

  /**
   * Read the three vocabularies a facet can be stated from.
   *
   * A failure leaves the three selects disabled with one sentence saying so, and the rest of the step still
   * works: a creator who cannot reach the vocabulary can still ask for an idea and keep parts of it.
   */
  protected async loadVocabulary(): Promise<void> {
    this.vocabulary.set({ status: 'loading' });

    const [cuisines, courses, techniques] = await Promise.all([
      this.reference.listCuisines(),
      this.reference.listCourses(),
      this.reference.listTechniques(),
    ]);

    this.vocabulary.set(
      cuisines.status === 'found' && courses.status === 'found' && techniques.status === 'found'
        ? {
            status: 'ready',
            cuisines: cuisines.entries,
            courses: courses.entries,
            techniques: techniques.techniques,
          }
        : { status: 'unavailable' },
    );
  }

  /** Read the lists the idea's own rows are changed from. Each lands, or does not, on its own. */
  private async loadRowCatalogues(): Promise<void> {
    const active = (entries: readonly { key: string; displayName: string; isActive: boolean }[]): readonly FacetOption[] =>
      entries.filter((entry) => entry.isActive).map((entry) => ({ value: entry.key, label: entry.displayName }));

    const [styles, occasions, channels] = await Promise.all([
      this.reference.listPhotographyStyles(),
      this.reference.listOccasions(),
      this.brandProfiles.listContentChannels(),
    ]);

    this.rowCatalogues.set({
      photographyStyle: styles.status === 'found' ? active(styles.entries) : null,
      occasion: occasions.status === 'found' ? active(occasions.entries) : null,
      channel: channels.status === 'found' ? active(channels.channels) : null,
    });
  }

  /**
   * Change one part of the idea to a value the creator chose.
   *
   * The same record a Keep writes, holding a different value — so the effect that follows the pins asks for
   * the idea again under its own code, and only this part moves.
   */
  protected setFacet(name: FacetRow['name'], value: string): void {
    if (name === 'theme' || value === '' || this.accepted() !== null) return;

    const draft = this.draft();
    if (name === 'channel') {
      this.changed.emit({ ...draft, config: { ...draft.config, channelKey: value } });
    } else if (name === 'day') {
      this.changed.emit({ ...draft, config: { ...draft.config, day: value as DayOfWeek } });
    } else {
      this.changed.emit({ ...draft, seed: { ...draft.seed, keep: { ...draft.seed.keep, [name]: value } } });
    }
  }

  /**
   * Ask for the typed name to be read.
   *
   * What comes back is kept by its request id and the name it was for; filling the controls is the effect's
   * job, once the draft says which reading this is.
   */
  private async readName(subject: string): Promise<void> {
    this.nameAskedFor = subject;

    const requestId = await this.nameTracker.submit(() =>
      this.dishFacets.request(this.workspaceSlug(), subject, this.nameKey.next()),
    );

    if (requestId === null) {
      // A reused key is the one refusal a fresh key fixes; every other failure keeps it so a retry replays.
      if (this.nameTracker.phase().kind === 'key_reused') this.nameKey.clear();
      return;
    }

    this.nameKey.clear();
    const draft = this.draft();
    this.changed.emit({ ...draft, seed: { ...draft.seed, nameReading: { requestId, subject, applied: false } } });
  }

  /** The creator's to press, after a reading that did not arrive. */
  protected readNameAgain(): void {
    const subject = this.subject();
    if (subject === null || this.nameTracker.busy()) return;

    const kept = this.draft().seed.nameReading ?? null;
    if (kept !== null && kept.subject === subject) {
      this.nameTracker.resume(kept.requestId);
      return;
    }

    void this.readName(subject);
  }

  /**
   * State a facet outright, or go back to letting the idea suggest one.
   *
   * Written into the same `keep` map the Keep buttons use, so there is exactly one record of what the creator
   * has asked for and the two controls cannot disagree. The effect above then re-asks under the same code, so
   * the change lands on the idea without a second click.
   */
  protected setStated(name: StatableFacet, code: string): void {
    const draft = this.draft();
    const keep = { ...draft.seed.keep };

    if (code === '') {
      delete keep[name];
    } else {
      keep[name] = code;
    }

    this.changed.emit({ ...draft, seed: { ...draft.seed, keep } });
  }

  protected readonly rows = computed<readonly FacetRow[]>(() => {
    const seed = this.seed();
    if (seed === null) return [];

    const rows: FacetRow[] = [];
    const vocabulary = this.facetOptions();
    const catalogues = this.rowCatalogues();
    const entries = (list: readonly ReferenceEntry[]): readonly FacetOption[] =>
      list.map((entry) => ({ value: entry.code, label: entry.displayName }));
    const lists: Readonly<Record<ContentSeedKeepName | 'channel', readonly FacetOption[]>> = {
      cuisine: entries(vocabulary.cuisine),
      dishType: entries(vocabulary.dishType),
      method: entries(vocabulary.method),
      photographyStyle: catalogues.photographyStyle ?? [],
      occasion: catalogues.occasion ?? [],
      channel: catalogues.channel ?? [],
    };
    // What the part is set to now is always among its choices, even where the catalogue has since retired it:
    // a picker that could not show its own value would show a different one as chosen.
    const withCurrent = (list: readonly FacetOption[], value: string, label: string): readonly FacetOption[] =>
      list.length === 0 || list.some((option) => option.value === value) ? list : [{ value, label }, ...list];

    const push = (name: ContentSeedKeepName | 'channel', facet: ContentSeedFacet | null): void => {
      if (facet === null) return;
      rows.push({
        name,
        label: CONTENT_SEED_FACET_LABELS[name],
        displayName: facet.displayName,
        key: facet.key,
        options: facet.fromRecipe ? [] : withCurrent(lists[name], facet.key, facet.displayName),
        pinned: facet.pinned,
        keepable: !facet.fromRecipe,
        fromRecipe: facet.fromRecipe,
      });
    };

    push('cuisine', seed.cuisine);
    push('dishType', seed.dishType);
    push('method', seed.method);
    push('photographyStyle', seed.photographyStyle);
    push('channel', seed.channel);
    push('occasion', seed.occasion);

    if (!this.showDay()) return rows;

    rows.push({
      name: 'day',
      label: CONTENT_SEED_FACET_LABELS.day,
      displayName: seed.day.day,
      key: seed.day.day,
      options: DAY_OPTIONS,
      pinned: seed.day.pinned,
      keepable: true,
      fromRecipe: false,
    });

    // The theme is not keepable: it follows whichever day is chosen, and it belongs to the workspace's own week
    // rather than to this idea.
    if (seed.day.theme !== null) {
      rows.push({
        name: 'theme',
        label: CONTENT_SEED_FACET_LABELS.theme,
        displayName: seed.day.theme.displayName,
        key: seed.day.theme.key,
        options: [],
        pinned: false,
        keepable: false,
        fromRecipe: false,
      });
    }

    return rows;
  });

  /** True when guidance about this idea's method must carry an explicit caution. */
  protected readonly needsSafetyCaution = computed(() => this.seed()?.method?.requiresSafetyCaution === true);

  /** The pins the server named when it refused, each with its own sentence and a way to let it go. */
  protected readonly refusedPins = computed<readonly RefusedPin[]>(() => {
    const state = this.state();
    if (state.status !== 'refused') return [];

    const pins: RefusedPin[] = [];
    for (const name of [...CONTENT_SEED_KEEP_NAMES, 'channel', 'day'] as readonly RefusableName[]) {
      const message = errorFor(state.fieldErrors, name);
      if (message !== '') pins.push({ name, label: CONTENT_SEED_FACET_LABELS[name], message });
    }

    return pins;
  });

  protected readonly tokenError = computed(() => {
    const state = this.state();
    if (state.status === 'refused') {
      const refused = errorFor(state.fieldErrors, 'token');
      if (refused !== '') return refused;
    }

    const entry = this.tokenEntry().trim();

    return entry === '' || isContentSeedToken(entry)
      ? ''
      : `A code is up to ${CONTENT_SEED_TOKEN_MAX_LENGTH} letters, digits, hyphens or underscores.`;
  });

  protected readonly canReproduce = computed(() => {
    const entry = this.tokenEntry().trim();
    return entry !== '' && isContentSeedToken(entry);
  });

  protected isKept(name: FacetRow['name']): boolean {
    if (name === 'channel') return this.draft().config.channelKey !== null;
    if (name === 'day') return this.draft().config.day !== null;
    if (name === 'theme') return false;

    return this.draft().seed.keep[name] !== undefined;
  }

  /**
   * Hold onto one part of the idea, or let it go.
   *
   * Channel and day are written back to the setup step rather than into the keep map, so each of those two
   * values has exactly one home and the setup step never disagrees with the idea on screen.
   */
  protected toggleKeep(name: FacetRow['name']): void {
    const seed = this.seed();
    if (seed === null || name === 'theme') return;

    const draft = this.draft();
    const kept = this.isKept(name);

    if (name === 'channel') {
      this.changed.emit({
        ...draft,
        config: { ...draft.config, channelKey: kept ? null : (seed.channel?.key ?? null) },
      });
      return;
    }

    if (name === 'day') {
      this.changed.emit({ ...draft, config: { ...draft.config, day: kept ? null : seed.day.day } });
      return;
    }

    const keep = { ...draft.seed.keep };
    if (kept) {
      delete keep[name];
    } else {
      const facet =
        name === 'cuisine'
          ? seed.cuisine
          : name === 'dishType'
            ? seed.dishType
            : name === 'method'
              ? seed.method
              : name === 'photographyStyle'
                ? seed.photographyStyle
                : seed.occasion;
      if (facet === null) return;
      keep[name] = facet.key;
    }

    this.changed.emit({ ...draft, seed: { ...draft.seed, keep } });
  }

  /** Stop keeping a pin the server refused, so the next try can succeed. */
  protected release(name: RefusableName): void {
    const draft = this.draft();

    if (name === 'channel') {
      this.changed.emit({ ...draft, config: { ...draft.config, channelKey: null } });
      return;
    }

    if (name === 'day') {
      this.changed.emit({ ...draft, config: { ...draft.config, day: null } });
      return;
    }

    const keep = { ...draft.seed.keep };
    delete keep[name];
    this.changed.emit({ ...draft, seed: { ...draft.seed, keep } });
  }

  /** A fresh idea: a new code from the server, with everything kept still kept. */
  protected tryAnother(): void {
    if (this.ideaAwaitsName()) return;

    const subject = this.subject();
    const kept = this.draft().seed.nameReading ?? null;
    // Read once per name, and not again after a reading that did not arrive: that retry has its own button.
    if (
      !this.readsNameOnArrival() &&
      this.offersStatedFacets() &&
      subject !== null &&
      this.nameAskedFor !== subject &&
      (kept === null || kept.subject !== subject) &&
      !this.nameTracker.busy()
    ) {
      this.ideaAwaitsName.set(true);
      void this.readName(subject);
      return;
    }

    this.run(null);
  }

  protected reproduce(): void {
    if (this.canReproduce()) this.run(this.tokenEntry().trim());
  }

  protected setTokenEntry(value: string): void {
    this.tokenEntry.set(value.slice(0, CONTENT_SEED_TOKEN_MAX_LENGTH));
  }


  /** Pick this idea. From here on it is part of the run rather than a suggestion. */
  protected accept(): void {
    const state = this.state();
    if (state.status !== 'shown') return;

    const draft = this.draft();
    // With no description there is nothing to choose between: the idea is what the picture is planned from,
    // and that is recorded as the choice it is rather than left for a later step to assume. A brief already
    // on the run — one resumed from another device, say — is the creator's and is left alone.
    const onlyIdea = draft.config.concept.trim() === '' && draft.config.briefSource === null && draft.config.brief === '';
    this.changed.emit({
      ...draft,
      config: onlyIdea ? { ...draft.config, briefSource: 'Idea', brief: state.seed.description } : draft.config,
      seed: { ...draft.seed, lastToken: state.seed.token, accepted: state.seed },
    });
    this.announced.emit('Idea picked.');
  }

  /**
   * Go back to looking. The idea stays on screen as a suggestion, so nothing has to be generated again.
   *
   * No confirmation: nothing is lost that one click cannot put back, and the steps that will depend on a picked
   * idea do not exist yet. When they do, this is where that question belongs.
   */
  protected changeIdea(): void {
    const accepted = this.accepted();
    if (accepted === null) return;

    const draft = this.draft();
    this.state.set({ status: 'shown', seed: accepted });
    this.changed.emit({ ...draft, seed: { ...draft.seed, accepted: null } });
    this.announced.emit('Looking for another idea.');
  }

  /**
   * Let go of a picked idea that is out of step with the linked recipe, and ask for one that is not.
   *
   * The creator's to press: the idea they picked is a decision, and nothing replaces it for them.
   */
  protected suggestForRecipe(): void {
    const draft = this.draft();
    if (draft.seed.accepted === null) return;

    this.changed.emit({ ...draft, seed: { ...draft.seed, accepted: null } });
    this.run(null);
  }

  /**
   * Choose what the picture is planned from.
   *
   * The choice makes the brief: the description as written, the idea's wording, or the description with the
   * idea's wording below it. **Neither source is changed** — the description stays the creator's own, which is
   * why this replaced an action that copied the idea over it. A brief they have since edited is theirs, so
   * replacing it asks first (.claude/rules/recipes.md, EASE-005).
   */
  protected async chooseBrief(values: readonly string[]): Promise<void> {
    const source = values[0] as ContentPipelineBriefSource | undefined;
    const draft = this.draft();
    const accepted = draft.seed.accepted;
    if (source === undefined || accepted === null || source === draft.config.briefSource) return;

    const brief = contentPipelineBriefFor(source, draft.config.concept, accepted.description);
    if (brief === null) return;

    if (isContentPipelineBriefEdited(draft)) {
      const replace = await this.confirm.confirm({
        title: 'Replace the brief you edited?',
        message: 'You changed the brief after choosing it. Choosing again puts it back to what this choice makes.',
        confirmLabel: 'Replace my edits',
        cancelLabel: 'Keep my edits',
      });
      if (!replace) {
        this.declined.update((count) => count + 1);
        return;
      }
    }

    // Re-read after the dialog: the draft could have moved on behind it.
    const latest = this.draft();
    this.changed.emit({ ...latest, config: { ...latest.config, briefSource: source, brief } });
    this.announced.emit(`${CONTENT_PIPELINE_BRIEF_SOURCE_LABELS[source]}. The brief is set.`);
  }

  /**
   * Ask for one idea.
   *
   * The request in flight is cancelled first: a creator who asks twice wants the second answer, and racing the
   * two would let the first one land on top of it.
   */
  private run(token: string | null): void {
    this.request?.unsubscribe();
    this.state.set({ status: 'generating' });

    const query = contentSeedQueryFor(this.draft(), token, this.recipe());
    this.request = this.seeds.generate(this.workspaceSlug(), query).subscribe((outcome) => {
      switch (outcome.status) {
        case 'found': {
          this.state.set({ status: 'shown', seed: outcome.seed });
          const draft = this.draft();
          // The code is kept, not the idea: it is what brings this same suggestion back after a refresh.
          this.changed.emit({ ...draft, seed: { ...draft.seed, lastToken: outcome.seed.token } });
          this.announced.emit('A new idea is ready.');
          break;
        }
        case 'refused':
          this.state.set({ status: 'refused', fieldErrors: outcome.fieldErrors });
          break;
        default:
          this.state.set({ status: 'unavailable' });
          break;
      }
    });
  }
}
