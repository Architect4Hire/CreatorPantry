import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { LongFormKind } from '../../models/long-form-draft.models';
import { LONG_FORM_TARGET_WORDS, LONG_FORM_TARGET_WORDS_LIMITS } from '../../models/long-form-pipeline.models';
import { CreativeContextSession } from '../../services/creative-context-session';
import { RecipePickerComponent } from '../../shared/recipe-picker/recipe-picker.component';
import { LongFormBrandReadComponent } from './long-form-brand-read.component';

/** `CreativeContextPolicy.WorkingTitleMaxLength`. */
export const LONG_FORM_TITLE_MAX = 200;

/** The creator's own description of the piece, as the creative context keeps it. */
export const LONG_FORM_BRIEF_MAX = 1000;

const KINDS: readonly CpChoiceOption[] = [
  { value: 'BlogArticle', label: 'A blog article', hint: 'One full piece, usually about one recipe.' },
  { value: 'NewsletterIssue', label: 'A newsletter issue', hint: 'A shorter piece to send: a greeting, what is new, a sign-off.' },
];

/**
 * The long-form pipeline's first step: what this piece is about (LF.6.2).
 *
 * **It asks only that.** Which kind of piece, the recipe it is about, and the creator's own words for it. The
 * voice and the audience are read from the brand and shown, never asked ({@link LongFormBrandReadComponent}).
 *
 * **Everything typed here is the creative context's.** The title and the brief are kept through the surface's
 * own {@link CreativeContextSession}, which files the work on a context at the first answer and saves each
 * edit after it; the recipe is a reference on that context. Nothing is held on this device. The kind and the
 * length are not the context's: they are what the creator asks the plan for, and travel with that request.
 *
 * **A piece needs no recipe.** A title or a few words of the creator's own are enough to plan from.
 *
 * **Once a plan has been asked for, the kind is settled.** A draft is an article or an issue from the moment
 * it exists, so the choice is shown and no longer offered.
 */
@Component({
  selector: 'cp-long-form-about-step',
  standalone: true,
  imports: [
    CpChoiceGroupComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    LongFormBrandReadComponent,
    RecipePickerComponent,
  ],
  templateUrl: './long-form-about-step.component.html',
  styles: [':host { display: grid; gap: var(--cp-space-8); } details p { margin: 0; }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LongFormAboutStepComponent {
  readonly workspaceSlug = input.required<string>();
  readonly session = input.required<CreativeContextSession>();
  readonly kind = input.required<LongFormKind>();

  /** The length the creator asked for, in words; null for the kind's own range. */
  readonly targetWords = input<number | null>(null);

  /** True once the piece has a draft: its kind was chosen when the plan was asked for. */
  readonly started = input(false);

  readonly idPrefix = input('cp-long-form-about');

  /** A recipe to offer, from the link that opened the writer (13A.18). Offered, never linked on arrival. */
  readonly suggestedRecipe = input<string | null>(null);

  readonly kindChange = output<LongFormKind>();
  readonly targetWordsChange = output<number | null>();
  readonly announced = output<string>();

  protected readonly kinds = KINDS;
  protected readonly titleMax = LONG_FORM_TITLE_MAX;
  protected readonly briefMax = LONG_FORM_BRIEF_MAX;
  protected readonly limits = LONG_FORM_TARGET_WORDS_LIMITS;

  protected readonly fields = computed(() => this.session().fields());

  protected readonly kindLabel = computed(() => KINDS.find((option) => option.value === this.kind())?.label ?? '');

  protected readonly usualLength = computed(() => {
    const range = LONG_FORM_TARGET_WORDS[this.kind()];

    return `${range.min} to ${range.max} words`;
  });

  /** A saved prompt the work already draws on — put there from the Prompt Library; this step adds none. */
  protected readonly drawsOnSavedPrompt = computed(
    () => this.session().context()?.references.some((reference) => reference.kind === 'PromptRecord') ?? false,
  );

  protected onKind(values: readonly string[]): void {
    const picked = values[0];

    if (picked === 'BlogArticle' || picked === 'NewsletterIssue') this.kindChange.emit(picked);
  }

  protected onTitle(event: Event): void {
    this.session().set({ ...this.fields(), workingTitle: (event.target as HTMLInputElement).value });
  }

  protected onBrief(event: Event): void {
    this.session().set({ ...this.fields(), pictureBrief: (event.target as HTMLTextAreaElement).value });
  }

  /** Empty means "the usual length". Anything outside the range is left for the field to say, and not sent. */
  protected onTargetWords(event: Event): void {
    const raw = (event.target as HTMLInputElement).value.trim();
    const words = Number(raw);

    this.targetWordsChange.emit(
      raw !== '' && Number.isInteger(words) && words >= this.limits.min && words <= this.limits.max ? words : null,
    );
  }
}
