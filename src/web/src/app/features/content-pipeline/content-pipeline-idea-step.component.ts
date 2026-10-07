import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_SEED_FACET_LABELS,
  CONTENT_SEED_KEEP_NAMES,
  ContentPipelineDraft,
  ContentSeedKeepName,
  contentSeedQueryFor,
} from '../../models/content-pipeline.models';
import {
  CONTENT_SEED_FIELD_NAMES,
  CONTENT_SEED_TOKEN_MAX_LENGTH,
  ContentSeed,
  ContentSeedFacet,
  isContentSeedToken,
} from '../../models/content-seed.models';
import { ContentSeedService } from '../../services/content-seed.service';

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
  /** The server's word: true when the creator asked for this value rather than the seed choosing it. */
  readonly pinned: boolean;
  /** False for the theme, which follows the day rather than being chosen. */
  readonly keepable: boolean;
}

type RefusableName = ContentSeedKeepName | 'channel' | 'day';

interface RefusedPin {
  readonly name: RefusableName;
  readonly label: string;
  readonly message: string;
}

/** The server names a refused pin in PascalCase; this reads its message back under our own name for it. */
function errorFor(
  fieldErrors: Record<string, readonly string[]>,
  name: keyof typeof CONTENT_SEED_FIELD_NAMES,
): string {
  return fieldErrors[CONTENT_SEED_FIELD_NAMES[name]]?.[0] ?? '';
}

/**
 * Step 2 of the Content Pipeline: an idea to start from, and the creator's decision about it (PIPE-UI-002).
 *
 * **An idea is a suggestion until it is picked.** The generator writes nothing, calls no model, and has no
 * record to fetch later — so nothing is kept while an idea is merely on screen except the code that reproduces
 * it. Picking one is the moment it becomes part of the run.
 *
 * **Keep and try again, rather than edit.** Each part of an idea can be held onto and the rest re-chosen around
 * it. Two of them — the channel and the day — are answers that already have a home on the previous step, so
 * keeping one writes it there instead of storing it twice.
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
 */
@Component({
  selector: 'cp-content-pipeline-idea-step',
  standalone: true,
  imports: [
    FormsModule,
    CpBadgeComponent,
    CpButtonComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
  ],
  templateUrl: './content-pipeline-idea-step.component.html',
  styleUrl: './content-pipeline-idea-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineIdeaStepComponent implements OnInit {
  private readonly seeds = inject(ContentSeedService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  readonly changed = output<ContentPipelineDraft>();
  /** One sentence for the shell's polite live region, so a change of idea is announced once. */
  readonly announced = output<string>();

  protected readonly tokenMaxLength = CONTENT_SEED_TOKEN_MAX_LENGTH;
  protected readonly state = signal<IdeaState>({ status: 'idle' });
  /** A code the creator typed or pasted, to come back to an idea they or someone else already had. */
  protected readonly tokenEntry = signal('');

  private request: Subscription | null = null;

  protected readonly accepted = computed(() => this.draft().seed.accepted);

  /** The idea on screen: the picked one if there is one, otherwise whatever was last generated. */
  protected readonly seed = computed<ContentSeed | null>(() => {
    const accepted = this.accepted();
    if (accepted !== null) return accepted;
    const state = this.state();

    return state.status === 'shown' ? state.seed : null;
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.request?.unsubscribe());
  }

  ngOnInit(): void {
    // A picked idea needs nothing fetched; it is stored whole. An unpicked one is reproduced from the code the
    // draft kept, which is what the contract offers in place of storing a suggestion.
    const { accepted, lastToken } = this.draft().seed;
    if (accepted === null && lastToken !== null) this.run(lastToken);
  }

  protected readonly rows = computed<readonly FacetRow[]>(() => {
    const seed = this.seed();
    if (seed === null) return [];

    const rows: FacetRow[] = [];
    const push = (name: ContentSeedKeepName | 'channel', facet: ContentSeedFacet | null): void => {
      if (facet === null) return;
      rows.push({
        name,
        label: CONTENT_SEED_FACET_LABELS[name],
        displayName: facet.displayName,
        pinned: facet.pinned,
        keepable: true,
      });
    };

    push('cuisine', seed.cuisine);
    push('dishType', seed.dishType);
    push('method', seed.method);
    push('photographyStyle', seed.photographyStyle);
    push('channel', seed.channel);
    push('occasion', seed.occasion);

    rows.push({
      name: 'day',
      label: CONTENT_SEED_FACET_LABELS.day,
      displayName: seed.day.day,
      pinned: seed.day.pinned,
      keepable: true,
    });

    // The theme is not keepable: it follows whichever day is chosen, and it belongs to the workspace's own week
    // rather than to this idea.
    if (seed.day.theme !== null) {
      rows.push({
        name: 'theme',
        label: CONTENT_SEED_FACET_LABELS.theme,
        displayName: seed.day.theme.displayName,
        pinned: false,
        keepable: false,
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
    this.changed.emit({
      ...draft,
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
   * Copy the idea's wording into the creator's own concept.
   *
   * Never automatic, and never silent: the concept is the creator's own words and this would replace them, so
   * where there is something there it asks first (.claude/rules/recipes.md, EASE-005).
   */
  protected async useAsConcept(): Promise<void> {
    const seed = this.seed();
    if (seed === null) return;

    const draft = this.draft();
    if (draft.config.concept.trim() !== '') {
      const replace = await this.confirm.confirm({
        title: 'Replace what you wrote?',
        message:
          'This will put the idea’s wording in place of the description you wrote. Your own words will be gone.',
        confirmLabel: 'Replace my description',
        cancelLabel: 'Keep my description',
      });
      if (!replace) return;
    }

    this.changed.emit({ ...draft, config: { ...draft.config, concept: seed.description } });
    this.announced.emit('The idea’s wording is now your description.');
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

    const query = contentSeedQueryFor(this.draft(), token);
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
