import { ChangeDetectionStrategy, Component, OnInit, effect, input, output, signal } from '@angular/core';
import { CpCheckboxComponent, CpFieldComponent, CpFormSectionComponent } from '@creator-pantry/ui';

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
  withChoice,
  withNote,
} from './brand-setup-style';

/**
 * Step 2 of "Create my voice": how the creator likes to sound, asked as a short run of pick-the-one-that-sounds-
 * like-you questions. Every question is optional, so Continue is always available. The shell owns Continue and
 * the autosave; this step only reports.
 */
@Component({
  selector: 'cp-brand-setup-style-step',
  standalone: true,
  imports: [CpCheckboxComponent, CpFieldComponent, CpFormSectionComponent],
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

  protected picked(question: StyleQuestion, key: string): boolean {
    return this.answers().choices[question.key].includes(key);
  }

  protected none(question: StyleQuestion): boolean {
    return this.answers().choices[question.key].length === 0;
  }

  /** At the cap, the remaining tones are disabled rather than silently dropped. */
  protected atCap(question: StyleQuestion, key: string): boolean {
    return question.multiple && this.answers().choices[question.key].length >= TONES_MAX && !this.picked(question, key);
  }

  protected choose(question: StyleQuestion, key: string | null, on = true): void {
    this.answers.update((a) => withChoice(a, question, key, on));
  }

  protected setNote(key: StyleQuestionKey, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.answers.update((a) => withNote(a, key, value));
  }
}
