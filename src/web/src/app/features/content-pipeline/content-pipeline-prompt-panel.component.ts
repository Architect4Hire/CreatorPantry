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
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { AiOperationStatusComponent } from '../ai/ai-operation-status.component';
import { AiOperationTracker } from '../ai/ai-operation-tracker';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_PIPELINE_LIMITS,
  ContentPipelineConfig,
  ContentPipelinePromptState,
  contentSubjectOf,
  isGeneratedPromptSource,
  promptReplacementNeedsAsking,
} from '../../models/content-pipeline.models';
import { LinkedRecipe } from '../../models/creative-context.models';
import { composedPromptWarnings, decodeComposedImagePrompt } from '../../models/image-prompt.models';
import { PHOTOGRAPHY_SHOT_KIND_LABELS } from '../../models/photography-concept.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { ImagePromptService } from '../../services/image-prompt.service';
import { IdempotencyKey } from './content-pipeline-idempotency';

/**
 * The prompt, composed and then the creator's (IMG-002).
 *
 * **Their edit is the prompt that counts.** What the model composed is kept beside it so they can see what they
 * changed and put it back, but `finalPrompt` is authoritative from the moment they touch it — and nothing
 * replaces it without asking.
 *
 * **The box is seeded only when it is empty.** A composition that landed on top of a creator's own wording would
 * be the rewrite .claude/rules/recipes.md forbids, dressed up as a convenience.
 *
 * **No rendering setting appears anywhere**, because the contract has nowhere to put one: no provider, model,
 * seed, sampler or dimension. IMG-002 composes text and renders nothing, and this panel saves nothing to the
 * prompt library either — that is a separate action.
 */
@Component({
  selector: 'cp-content-pipeline-prompt-panel',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpFieldComponent,
    CpNoticeComponent,
    AiOperationStatusComponent,
    AiAllowanceNoticeComponent,
  ],
  templateUrl: './content-pipeline-prompt-panel.component.html',
  styleUrl: './content-pipeline-prompt-panel.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelinePromptPanelComponent implements OnInit {
  private readonly prompts = inject(ImagePromptService);
  private readonly usage = inject(AiUsageService);
  private readonly confirm = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly config = input.required<ContentPipelineConfig>();
  readonly prompt = input.required<ContentPipelinePromptState>();
  /**
   * The recipe the picture is of, where the creator linked one, with the version pinned when they did (AF.3.3).
   * Sent as `recipeId` and `recipeVersionId`; null sends neither.
   */
  readonly recipe = input<LinkedRecipe | null>(null);
  readonly changed = output<ContentPipelinePromptState>();
  readonly announced = output<string>();

  protected readonly tracker = new AiOperationTracker((requestId) =>
    this.prompts.watch(this.workspaceSlug(), requestId),
  );

  protected readonly allowance = this.usage.allowance;
  protected readonly shotLabels = PHOTOGRAPHY_SHOT_KIND_LABELS;

  /** The box's bound is the image route's own, so the prompt cannot grow past what the next step can send. */
  protected readonly limits = CONTENT_PIPELINE_LIMITS;

  private readonly idempotency = new IdempotencyKey();

  constructor() {
    this.destroyRef.onDestroy(() => this.tracker.destroy());

    // What came back, kept and — only into an empty box — offered as a starting point. Seeding a box the
    // creator has written in is the one thing this must never do on its own.
    effect(() => {
      const composed = decodeComposedImagePrompt(this.tracker.proposal());
      if (composed === null) return;

      const prompt = this.prompt();
      const alreadyKept = prompt.generated?.text === composed.prompt;
      const seed = prompt.finalPrompt.trim() === '' && prompt.promptSource === 'none';
      if (alreadyKept && !seed) return;

      this.changed.emit({
        ...prompt,
        generated: { text: composed.prompt, avoid: composed.avoid },
        finalPrompt: seed ? composed.prompt : prompt.finalPrompt,
        // Seeded text is the model's, and is labelled as such until the creator touches it.
        promptSource: seed ? 'composed' : prompt.promptSource,
      });
    });
  }

  ngOnInit(): void {
    void this.usage.ensureLoaded();

    const requestId = this.prompt().promptRequestId;
    if (requestId !== null) this.tracker.resume(requestId);
  }

  protected readonly chosen = computed(() => this.prompt().chosen);

  protected readonly canCompose = computed(() => this.chosen() !== null && !this.tracker.busy());

  /** True when what was written differs from what is in the box, so offering it is worth something. */
  protected readonly differsFromGenerated = computed(() => {
    const prompt = this.prompt();

    return prompt.generated !== null && prompt.generated.text !== prompt.finalPrompt;
  });

  /**
   * What the creator should know about this composition.
   *
   * Read straight off the proposal rather than off the decoded prompt: a proposal can carry the warning that
   * explains why there is no usable prompt, and hanging warnings off the prompt would drop exactly those
   * (.claude/rules/ai.md).
   */
  protected readonly warnings = computed(() => composedPromptWarnings(this.tracker.proposal()));

  /** True while the words in the box are not the creator's, so the screen has to say so. */
  protected readonly promptIsGenerated = computed(() => isGeneratedPromptSource(this.prompt().promptSource));

  /** Which of the two generated sources the box holds, for the sentence that labels it. */
  protected readonly generatedFrom = computed(() =>
    this.prompt().promptSource === 'reference' ? 'your reference photograph' : 'the look you picked',
  );

  protected readonly quotaRefusal = computed(() => {
    const phase = this.tracker.phase();

    return phase.kind === 'blocked' ? phase.refusal : null;
  });

  protected readonly refusalMessage = computed(() => {
    const phase = this.tracker.phase();
    if (phase.kind !== 'refused') return '';

    const fields = Object.values(phase.fieldErrors).flat();

    return phase.message || fields[0] || 'That prompt could not be asked for.';
  });

  /**
   * Compose, or compose again.
   *
   * Asks first when the creator has edited the prompt, because what comes back would be offered in place of
   * their words — and a creator who recomposed by reflex should not lose them to it.
   */
  /** A deliberate new question, so it takes a fresh key rather than replaying the last answer. */
  protected composeAgain(): void {
    this.idempotency.clear();
    void this.compose();
  }

  protected async compose(): Promise<void> {
    const prompt = this.prompt();
    // Guarded before the dialog so nothing is asked about a prompt there is no shot for, and again after it.
    if (prompt.chosen === null) return;

    if (promptReplacementNeedsAsking(prompt)) {
      const replace = await this.confirm.confirm({
        title: 'Write it again?',
        message:
          'A new prompt will be written for this shot. The one you have edited stays until you replace it yourself.',
        confirmLabel: 'Write it again',
        cancelLabel: 'Keep what I have',
        tone: 'neutral',
      });
      if (!replace) return;
    }

    // Re-read after the dialog: the brief or the pick could have changed behind a modal, and composing from the
    // snapshot would ask about something other than what is on screen.
    const latest = this.prompt();
    const pick = latest.chosen;
    if (pick === null) return;

    const config = this.config();
    const recipe = this.recipe();
    const requestId = await this.tracker.submit(() =>
      this.prompts.request(
        this.workspaceSlug(),
        {
          conceptRequestId: pick.conceptRequestId,
          conceptId: pick.conceptId,
          shotKind: pick.shotKind,
          channelKey: config.channelKey,
          recipeId: recipe?.recipeId ?? null,
          recipeVersionId: recipe?.recipeVersionId ?? null,
          dishName: contentSubjectOf(config, recipe),
          briefDocumentId: latest.brief?.documentId ?? null,
          sceneOverrides: config.scene,
          styleOverrides: config.style,
        },
        this.idempotency.next(),
      ),
    );

    if (requestId === null) {
      if (this.tracker.phase().kind === 'key_reused') this.idempotency.clear();
      return;
    }

    this.idempotency.clear();
    this.changed.emit({ ...this.prompt(), promptRequestId: requestId });
    this.announced.emit('Writing a prompt for the shot you picked.');
  }

  /** Every keystroke. The moment they type, the prompt is theirs and nothing may replace it unasked. */
  protected setFinalPrompt(text: string): void {
    this.changed.emit({ ...this.prompt(), finalPrompt: text, promptSource: 'creator' });
  }

  /**
   * Put the composed wording back.
   *
   * Offered rather than imposed, and it asks, because it discards whatever the creator wrote over it. The
   * composed text is kept precisely so this is possible.
   */
  protected async restoreGenerated(): Promise<void> {
    const prompt = this.prompt();
    const generated = prompt.generated;
    if (generated === null) return;

    const replace = await this.confirm.confirm({
      title: 'Use the written prompt?',
      message: 'What is in the box now will be replaced by the wording we wrote.',
      confirmLabel: 'Use that wording',
      cancelLabel: 'Keep what I have',
    });
    if (!replace) return;

    // Re-read rather than reusing the snapshot taken before the dialog: the creator could have typed behind it.
    const latest = this.prompt();
    const current = latest.generated ?? generated;
    this.changed.emit({ ...latest, finalPrompt: current.text, promptSource: 'composed' });
    this.announced.emit('The written prompt is in the box.');
  }

  protected stopChecking(): void {
    this.tracker.stopChecking();
  }

  protected resume(): void {
    const requestId = this.tracker.requestId();
    if (requestId !== null) this.tracker.resume(requestId);
  }
}
