import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import {
  CONTENT_PIPELINE_LIMITS,
  ContentPipelineConfig,
  VARIANT_COUNT_CHOICES,
} from '../../models/content-pipeline.models';
import { DAYS_OF_WEEK, DayOfWeek } from '../../models/content-seed.models';
import { BrandProfileService } from '../../services/brand-profile.service';

/** One editable row in a scene or style list. The id keeps a field's label and error attached while rows move. */
interface OverrideRow {
  readonly id: string;
  readonly text: string;
}

let nextRowId = 0;

function rowsFrom(values: readonly string[]): readonly OverrideRow[] {
  return values.map((text) => ({ id: `r${(nextRowId += 1)}`, text }));
}

function sameList(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

type ChannelsState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly channels: readonly ContentChannel[] }
  | { readonly status: 'unavailable' };

/**
 * What a picture is of, where the post is going, which day, how many pictures, and anything the creator
 * already knows they want in the shot (PIPE-UI-001).
 *
 * **Asked in one place or two, as its host says** ({@link show}). The Image Studio asks all of it on one
 * screen. The Content Pipeline asks only what the run is about on its first step, and shows the rest as the
 * brand's own answers — this component is then what its "Adjust for this run" drawer holds.
 *
 * **Every field is optional, and that is the capability's shape** — a creator may be planning a shoot for a
 * finished recipe, a draft, or an idea with nothing behind it yet. The legend above says so once; no field
 * marks itself optional.
 *
 * A controlled step: it renders the config handed in and emits the next one. It holds no copy of the answers
 * apart from the editing rows, so the shell stays the single source of truth and what is on screen is always
 * what will be kept.
 *
 * **The channel list is the one thing it loads.** A list that cannot be read leaves the field disabled with a
 * sentence saying so, and the rest of the step still works — a creator who cannot reach the vocabulary can
 * still describe their picture.
 */
@Component({
  selector: 'cp-content-pipeline-setup-step',
  standalone: true,
  imports: [
    CpSkeletonComponent,
    FormsModule,
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    RouterLink,
  ],
  templateUrl: './content-pipeline-setup-step.component.html',
  styleUrl: './content-pipeline-setup-step.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineSetupStepComponent implements OnInit {
  private readonly brandProfiles = inject(BrandProfileService);

  readonly config = input.required<ContentPipelineConfig>();
  /**
   * The linked recipe's title, or null when none is linked.
   *
   * Only ever shown, in the hint beside the name: a creator who has linked a recipe needs telling that the
   * recipe is what the work is planned around, so a name they typed earlier does not read as the answer in
   * use. The precedence itself is `contentSubjectOf`'s, not this component's.
   */
  readonly linkedRecipeTitle = input<string | null>(null);
  /**
   * False where a day means nothing. The Image Studio asks the same questions about a picture, but it plans no
   * week and has no idea step for a day's theme to feed, so offering one there would be a field with no effect.
   */
  readonly showDay = input(true);
  /**
   * Which of its questions this instance asks.
   *
   * `subject` is what a run is about — its name, its day, the picture in mind. `defaults` is what a run starts
   * with from the brand and may change for itself — the channel, the count, scene and style. The Content
   * Pipeline asks them in two places, the second only when the creator wants to change something; the Image
   * Studio asks `everything` on one screen.
   */
  readonly show = input<'everything' | 'subject' | 'defaults'>('everything');
  /**
   * How many pictures the run tries until the creator says otherwise here, or null to show the config's own.
   * The brand's default: shown as the answer already given, and replaced by the first choice made.
   */
  readonly pictureCountDefault = input<number | null>(null);
  /**
   * Where the workspace's themed days are managed, or null to offer no way there.
   *
   * Handed in rather than built here: this step is hosted by more than one page and reads no route of its own,
   * so only its host knows which workspace the link belongs to.
   */
  readonly themedDaysLink = input<readonly string[] | null>(null);
  /**
   * Where recipe ideas for this run's work are asked for, or null when the run has no filed work yet. Handed
   * in for the same reason as {@link themedDaysLink}: only the host knows the work and the workspace.
   */
  readonly recipeIdeasLink = input<readonly string[] | null>(null);
  readonly changed = output<ContentPipelineConfig>();

  protected readonly limits = CONTENT_PIPELINE_LIMITS;
  protected readonly days = DAYS_OF_WEEK;

  protected readonly channels = signal<ChannelsState>({ status: 'loading' });

  /**
   * The rows being edited, which the config alone cannot express: a row the creator has just added, or just
   * emptied, is blank, and a blank entry is dropped on the way out to the config.
   */
  protected readonly sceneRows = signal<readonly OverrideRow[]>([]);
  protected readonly styleRows = signal<readonly OverrideRow[]>([]);

  protected readonly variantChoices: readonly CpChoiceOption[] = VARIANT_COUNT_CHOICES.map((count) => ({
    value: String(count),
    label: count === 1 ? '1 picture' : `${count} pictures`,
  }));

  protected readonly dayChoices: readonly CpChoiceOption[] = [
    { value: '', label: 'No particular day' },
    ...DAYS_OF_WEEK.map((day) => ({ value: day, label: day })),
  ];

  protected readonly channelOptions = computed(() => {
    const state = this.channels();
    if (state.status !== 'ready') return [] as readonly ContentChannel[];
    const chosen = this.config().channelKey;

    // A retired channel is shown where it is already chosen and never offered as a new choice.
    return state.channels.filter((channel) => channel.isActive || channel.key === chosen);
  });

  /**
   * What the name field says about itself: which answer is in use, and what the other one is for.
   *
   * It never asks the creator to delete the name they typed. A linked recipe can be unlinked, and the name is
   * then the subject again — so saying it is kept is the honest sentence, and clearing it would be this screen
   * throwing away the creator's own words (.claude/rules/recipes.md).
   */
  protected readonly subjectHint = computed(() => {
    const title = this.linkedRecipeTitle();

    return title === null
      ? `A dish name, up to ${CONTENT_PIPELINE_LIMITS.subjectMaxLength} characters. Use it when the recipe is not in your library yet.`
      : `Planned around “${title}” from your library instead. This name is kept, and used again if you unlink that recipe.`;
  });

  protected readonly asksSubject = computed(() => this.show() !== 'defaults');
  protected readonly asksDefaults = computed(() => this.show() !== 'subject');

  /** The section that holds the channel, the day, or both, named for what it is asking here. */
  protected readonly placeHeading = computed(() => (this.asksDefaults() ? 'Where it is going' : 'When it is for'));
  protected readonly placeIntro = computed(() =>
    this.asksDefaults()
      ? 'So the pictures and the words suit the place you publish.'
      : 'A day with a theme of yours gives the idea something to start from.',
  );

  protected readonly sceneAtCap = computed(() => this.sceneRows().length >= CONTENT_PIPELINE_LIMITS.maxOverrides);
  protected readonly styleAtCap = computed(() => this.styleRows().length >= CONTENT_PIPELINE_LIMITS.maxOverrides);


  constructor() {
    // Rows are seeded from the config, and re-seeded whenever the config's lists stop matching what is on
    // screen. That second case is the host replacing the draft — Start over, or moving to another workspace —
    // which must not leave a creator looking at the previous run's scene. It cannot fire while they type: a
    // blank row reads as no entry on both sides, so an empty row they just added survives.
    effect(() => {
      const config = this.config();
      if (!sameList(config.scene, this.textOf(this.sceneRows()))) this.sceneRows.set(rowsFrom(config.scene));
      if (!sameList(config.style, this.textOf(this.styleRows()))) this.styleRows.set(rowsFrom(config.style));
    });
  }

  ngOnInit(): void {
    if (this.asksDefaults()) void this.loadChannels();
  }

  protected async loadChannels(): Promise<void> {
    this.channels.set({ status: 'loading' });
    const outcome = await this.brandProfiles.listContentChannels();
    this.channels.set(
      outcome.status === 'found' ? { status: 'ready', channels: outcome.channels } : { status: 'unavailable' },
    );
  }

  protected setChannel(key: string): void {
    this.emit({ channelKey: key === '' ? null : key });
  }

  protected setDay(values: readonly string[]): void {
    const value = values[0] ?? '';
    this.emit({ day: value === '' ? null : (value as DayOfWeek) });
  }

  protected setVariantCount(values: readonly string[]): void {
    const value = Number(values[0]);
    if (Number.isFinite(value)) this.emit({ variantCount: value, variantCountChosen: true });
  }

  protected setSubject(text: string): void {
    this.emit({ subject: text.slice(0, CONTENT_PIPELINE_LIMITS.subjectMaxLength) });
  }

  protected setConcept(text: string): void {
    this.emit({ concept: text.slice(0, CONTENT_PIPELINE_LIMITS.conceptMaxLength) });
  }

  protected addScene(): void {
    if (this.sceneAtCap()) return;
    this.sceneRows.update((rows) => [...rows, { id: `r${(nextRowId += 1)}`, text: '' }]);
  }

  protected addStyle(): void {
    if (this.styleAtCap()) return;
    this.styleRows.update((rows) => [...rows, { id: `r${(nextRowId += 1)}`, text: '' }]);
  }

  protected setScene(id: string, text: string): void {
    this.sceneRows.update((rows) => rows.map((row) => (row.id === id ? { ...row, text } : row)));
    this.emit({ scene: this.textOf(this.sceneRows()) });
  }

  protected setStyle(id: string, text: string): void {
    this.styleRows.update((rows) => rows.map((row) => (row.id === id ? { ...row, text } : row)));
    this.emit({ style: this.textOf(this.styleRows()) });
  }

  protected removeScene(id: string): void {
    this.sceneRows.update((rows) => rows.filter((row) => row.id !== id));
    this.emit({ scene: this.textOf(this.sceneRows()) });
  }

  protected removeStyle(id: string): void {
    this.styleRows.update((rows) => rows.filter((row) => row.id !== id));
    this.emit({ style: this.textOf(this.styleRows()) });
  }

  protected dayValue(): readonly string[] {
    return [this.config().day ?? ''];
  }

  protected variantValue(): readonly string[] {
    const config = this.config();
    const fallback = this.pictureCountDefault();

    return [String(config.variantCountChosen === true || fallback === null ? config.variantCount : fallback)];
  }

  /** Blank rows are dropped on the way out: an empty row is one the creator emptied, not an answer. */
  private textOf(rows: readonly OverrideRow[]): readonly string[] {
    return rows.map((row) => row.text.trim()).filter((text) => text.length > 0);
  }

  private emit(patch: Partial<ContentPipelineConfig>): void {
    this.changed.emit({ ...this.config(), ...patch });
  }
}
