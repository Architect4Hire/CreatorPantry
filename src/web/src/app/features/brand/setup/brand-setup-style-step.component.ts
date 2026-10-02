import { ChangeDetectionStrategy, Component, OnInit, effect, input, output, signal } from '@angular/core';
import { CpChoiceGroupComponent, CpChoiceOption, CpFieldComponent, CpFormSectionComponent } from '@creator-pantry/ui';

import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';
import {
  BLANK_ANSWERS,
  NOTE_MAX,
  STYLE_QUESTIONS,
  StyleAnswers,
  StyleQuestion,
  StyleQuestionKey,
  TONES_MAX,
  decodeStyle,
  sameStyle,
  withNote,
} from './brand-setup-style';

/**
 * Each question's options in the shape the library's choice group takes, built once.
 *
 * A single-answer question ends with a real "Not sure yet" tile. A radio cannot be unticked by clicking it, so
 * without one an answer given by mistake could not be taken back — and every question here is optional, which
 * has to mean answerable with "no answer" rather than only skippable by never touching it. It stands for the
 * empty answer and is never stored: see {@link UNSURE}.
 */
const UNSURE = 'unsure';

const CHOICES: ReadonlyMap<StyleQuestionKey, readonly CpChoiceOption[]> = new Map(
  STYLE_QUESTIONS.map((question) => [
    question.key,
    [
      ...question.choices.map((choice) => ({
        value: choice.key,
        label: choice.label,
        example: choice.example || undefined,
      })),
      ...(question.multiple ? [] : [{ value: UNSURE, label: 'Not sure yet' }]),
    ],
  ]),
);

/**
 * Step 2 of "Create my voice": how the creator likes to sound, asked as a short run of pick-the-one-that-sounds-
 * like-you questions. Every question is optional, so Continue is always available. The shell owns Continue and
 * the autosave; this step only reports.
 */
@Component({
  selector: 'cp-brand-setup-style-step',
  standalone: true,
  imports: [CpChoiceGroupComponent, CpFieldComponent, CpFormSectionComponent],
  templateUrl: './brand-setup-style-step.component.html',
  styleUrl: './brand-setup-style-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupStyleStepComponent implements OnInit {
  readonly step = input.required<BrandSetupStepDefinition>();
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  readonly reported = output<BrandSetupStepReport>();

  protected readonly questions = STYLE_QUESTIONS;
  protected readonly noteMax = NOTE_MAX;
  protected readonly tonesMax = TONES_MAX;

  protected readonly answers = signal<StyleAnswers>(BLANK_ANSWERS);
  private baseline: StyleAnswers = BLANK_ANSWERS;
  private hadSavedDraft = false;
  private ready = false;
  /** Once the creator has changed anything, every later state is reported, including a return to the start. */
  private edited = false;

  constructor() {
    effect(() => {
      const answers = this.answers();
      if (!this.ready) return;
      const dirty = !sameStyle(answers, this.baseline);
      // A blank form is not an answer: nothing is saved until the creator picks or writes something.
      if (dirty) this.edited = true;
      const draft = this.edited || this.hadSavedDraft ? ({ ...answers } as Record<string, unknown>) : null;
      this.reported.emit({ canContinue: true, isDirty: dirty, draft });
    });
  }

  ngOnInit(): void {
    const saved = this.draft();
    this.hadSavedDraft = saved !== null && Object.keys(saved).length > 0;
    this.baseline = decodeStyle(this.hadSavedDraft ? saved : null);
    this.answers.set(this.baseline);
    this.ready = true;
  }

  protected choicesFor(question: StyleQuestion): readonly CpChoiceOption[] {
    return CHOICES.get(question.key) ?? [];
  }

  /** What the group shows as picked: the stored answer, or "Not sure yet" when a single question has none. */
  protected shown(question: StyleQuestion): readonly string[] {
    const stored = this.answers().choices[question.key];
    return stored.length === 0 && !question.multiple ? [UNSURE] : stored;
  }

  protected setChoices(question: StyleQuestion, picked: readonly string[]): void {
    // "Not sure yet" is the empty answer, so it is stored as one rather than as a choice of its own.
    const next = picked.filter((value) => value !== UNSURE);
    this.answers.update((a) => ({ ...a, choices: { ...a.choices, [question.key]: next } }));
  }

  protected setNote(key: StyleQuestionKey, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.answers.update((a) => withNote(a, key, value));
  }
}
