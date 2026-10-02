import { ChangeDetectionStrategy, Component, OnInit, computed, effect, input, output, signal } from '@angular/core';
import { CpCheckboxComponent, CpFieldComponent, CpFormSectionComponent } from '@creator-pantry/ui';

import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';
import {
  AUDIENCE_OPTIONS,
  AVOID_MAX,
  CHANNEL_OPTIONS,
  DIFFERENT_MAX,
  GoalsAnswers,
  NOTE_MAX,
  PURPOSE_OPTIONS,
  WEBSITE_MAX,
  decodeGoals,
  goalsComplete,
  sameAnswers,
  toggled,
  websiteProblem,
} from './brand-setup-goals';

/**
 * Step 1 of "Create my voice": what the creator makes, who it is for, and where they share it.
 * Honours the step-body contract: the shell hands in this step's saved slice and receives `canContinue`,
 * `isDirty` and the slice back. The shell owns Continue and the autosave.
 */
@Component({
  selector: 'cp-brand-setup-goals-step',
  standalone: true,
  imports: [CpCheckboxComponent, CpFieldComponent, CpFormSectionComponent],
  templateUrl: './brand-setup-goals-step.component.html',
  styleUrl: './brand-setup-goals-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupGoalsStepComponent implements OnInit {
  readonly step = input.required<BrandSetupStepDefinition>();
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  readonly reported = output<BrandSetupStepReport>();

  protected readonly purposeOptions = PURPOSE_OPTIONS;
  protected readonly audienceOptions = AUDIENCE_OPTIONS;
  protected readonly channelOptions = CHANNEL_OPTIONS;
  protected readonly noteMax = NOTE_MAX;
  protected readonly differentMax = DIFFERENT_MAX;
  protected readonly avoidMax = AVOID_MAX;
  protected readonly websiteMax = WEBSITE_MAX;

  protected readonly answers = signal<GoalsAnswers>(decodeGoals(null));
  /** Where the creator started from: the saved answers, or the defaults. */
  private baseline: GoalsAnswers = decodeGoals(null);
  private hadSavedDraft = false;
  private ready = false;
  /** Once the creator has changed anything, every later state is reported, including a return to the start. */
  private edited = false;

  protected readonly websiteError = computed(() => websiteProblem(this.answers().website));
  protected readonly complete = computed(() => goalsComplete(this.answers()));
  protected readonly touched = signal(false);
  protected readonly hasDetails = computed(() => {
    const a = this.answers();
    return a.different !== '' || a.avoid !== '' || a.website !== '';
  });

  constructor() {
    effect(() => {
      const answers = this.answers();
      const complete = this.complete();
      if (!this.ready) return;
      const dirty = !sameAnswers(answers, this.baseline);
      // Untouched defaults are not an answer: nothing is saved until the creator changes something.
      if (dirty) this.edited = true;
      const draft = this.edited || this.hadSavedDraft ? ({ ...answers } as Record<string, unknown>) : null;
      this.reported.emit({ canContinue: complete, isDirty: dirty, draft });
    });
  }

  ngOnInit(): void {
    const saved = this.draft();
    this.hadSavedDraft = saved !== null && Object.keys(saved).length > 0;
    this.baseline = decodeGoals(this.hadSavedDraft ? saved : null);
    this.answers.set(this.baseline);
    this.ready = true;
  }

  protected has(list: readonly string[], key: string): boolean {
    return list.includes(key);
  }

  protected setPurpose(key: string, on: boolean): void {
    this.touched.set(true);
    this.answers.update((a) => ({ ...a, purposes: toggled(a.purposes, key, on, PURPOSE_OPTIONS) }));
  }

  protected setChannel(key: string, on: boolean): void {
    this.touched.set(true);
    this.answers.update((a) => ({ ...a, channels: toggled(a.channels, key, on, CHANNEL_OPTIONS) }));
  }

  protected setAudience(key: string): void {
    this.touched.set(true);
    this.answers.update((a) => ({ ...a, audience: key }));
  }

  protected setText(field: 'purposeNote' | 'audienceNote' | 'channelNote' | 'different' | 'avoid' | 'website', event: Event): void {
    this.touched.set(true);
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement).value;
    this.answers.update((a) => ({ ...a, [field]: value }));
  }
}
