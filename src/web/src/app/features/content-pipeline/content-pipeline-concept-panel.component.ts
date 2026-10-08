import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import { CpButtonComponent, CpChoiceGroupComponent, CpChoiceOption, CpNoticeComponent } from '@creator-pantry/ui';

import { AiOperationStatusComponent } from '../ai/ai-operation-status.component';
import { AiOperationTracker } from '../ai/ai-operation-tracker';
import { AiAllowanceNoticeComponent } from '../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { ContentPipelineConfig, ContentPipelinePromptState } from '../../models/content-pipeline.models';
import {
  PHOTOGRAPHY_SHOT_KIND_LABELS,
  PhotographyConcept,
  decodePhotographyConcepts,
  generalPhotographyWarnings,
} from '../../models/photography-concept.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { IdempotencyKey } from './content-pipeline-idempotency';

/** How one shot of one concept is addressed as a single choice. */
const PICK_SEPARATOR = '|';

/**
 * The look, and which shot of it to write a prompt for (IMG-001).
 *
 * **Concepts are planning material, not an edit.** There is no disposition route for one: a creator reads one to
 * three looks and picks a single shot to carry forward. Nothing here touches a recipe, and a concept has nowhere
 * to put a quantity, a time or a temperature — so none appears.
 *
 * **One pick, not two.** The concept and the shot are chosen in one gesture, because IMG-002 refuses a shot a
 * concept did not plan and choosing them separately would let a creator build that refusal by hand.
 *
 * **The request comes from the setup step**, so the channel, the scene and style lines and the creator's own
 * concept wording are the ones they already gave. Nothing is asked twice.
 *
 * **It is never sent empty.** Every field on the setup step is optional, so a creator can arrive here having
 * filled in none of them. Where they wrote no description, the idea they picked stands in as the brief — it is a
 * decision of theirs, and it is the only thing that says what the picture is of. Where there is no idea either
 * and no scene or style, nothing is asked: a request that names no subject is charged for and comes back with no
 * looks, which a creator can only read as something having broken.
 */
@Component({
  selector: 'cp-content-pipeline-concept-panel',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpNoticeComponent,
    AiOperationStatusComponent,
    AiAllowanceNoticeComponent,
  ],
  templateUrl: './content-pipeline-concept-panel.component.html',
  styleUrl: './content-pipeline-concept-panel.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineConceptPanelComponent implements OnInit {
  private readonly concepts = inject(PhotographyConceptService);
  private readonly usage = inject(AiUsageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly config = input.required<ContentPipelineConfig>();
  readonly prompt = input.required<ContentPipelinePromptState>();
  /**
   * The wording of the idea the creator picked, where the surface has one. Used as the brief only when they wrote
   * no description of their own, and never written into `config.concept` — those are their words, and this is not.
   */
  readonly idea = input<string | null>(null);
  readonly changed = output<ContentPipelinePromptState>();
  readonly announced = output<string>();

  protected readonly tracker = new AiOperationTracker((requestId) =>
    this.concepts.watch(this.workspaceSlug(), requestId),
  );

  protected readonly allowance = this.usage.allowance;
  protected readonly shotLabels = PHOTOGRAPHY_SHOT_KIND_LABELS;

  private readonly idempotency = new IdempotencyKey();

  constructor() {
    this.destroyRef.onDestroy(() => this.tracker.destroy());
  }

  ngOnInit(): void {
    void this.usage.ensureLoaded();

    // A proposal is durable and readable by its request id, so coming back to the step reads it again rather
    // than asking for a second set of concepts — which would spend the allowance twice for the same answer.
    const requestId = this.prompt().conceptRequestId;
    if (requestId !== null) this.tracker.resume(requestId);
  }

  /** The concepts on screen, read back out of the proposal's flat rows. */
  protected readonly conceptList = computed<readonly PhotographyConcept[]>(() =>
    decodePhotographyConcepts(this.tracker.proposal()),
  );

  /** Warnings about the answer as a whole. Those about one concept render with it. */
  protected readonly generalWarnings = computed(() => generalPhotographyWarnings(this.tracker.proposal()));

  /**
   * Every shot of every concept, as one set of choices.
   *
   * One group rather than one per concept, so there is exactly one answer: a creator cannot leave two concepts
   * each holding a pick and then wonder which one the prompt came from.
   */
  protected readonly shotChoices = computed<readonly CpChoiceOption[]>(() =>
    this.conceptList().flatMap((concept) =>
      concept.shots.map((shot) => ({
        value: `${concept.conceptId}${PICK_SEPARATOR}${shot.kind}`,
        label: `${concept.label} — ${PHOTOGRAPHY_SHOT_KIND_LABELS[shot.kind]}`,
        hint: shot.framing ?? undefined,
        example: shot.lighting ?? undefined,
        // A concept whose rows carried no id cannot be named to IMG-002, so it is offered disabled rather than
        // as a pick that would be refused on submit.
        disabled: concept.conceptId === '',
      })),
    ),
  );

  protected readonly pickValue = computed<readonly string[]>(() => {
    const chosen = this.prompt().chosen;

    return chosen === null ? [] : [`${chosen.conceptId}${PICK_SEPARATOR}${chosen.shotKind}`];
  });

  protected readonly hasPick = computed(() => this.prompt().chosen !== null);

  /** What the picture is of: the creator's own description, or failing that the idea they picked. */
  private readonly brief = computed(() => {
    const concept = this.config().concept;

    return concept.trim() === '' ? (this.idea() ?? '') : concept;
  });

  /**
   * Whether the request would name anything to photograph. A channel alone does not: it says where the picture
   * is going, not what is in it.
   */
  protected readonly hasSubject = computed(() => {
    const { scene, style } = this.config();
    const said = (line: string): boolean => line.trim() !== '';

    return said(this.brief()) || scene.some(said) || style.some(said);
  });

  /** Says why asking is unavailable, except while a request is already on its way and nothing can be asked. */
  protected readonly needsSubject = computed(() => {
    const kind = this.tracker.phase().kind;

    return !this.hasSubject() && kind !== 'submitting' && kind !== 'watching';
  });

  /** True when there is nothing in the way of asking: something to plan around, and nothing already in flight. */
  protected readonly canAsk = computed(() => !this.tracker.busy() && this.hasSubject());

  protected readonly refusalFieldErrors = computed(() => {
    const phase = this.tracker.phase();

    return phase.kind === 'refused' ? Object.values(phase.fieldErrors).flat() : [];
  });

  protected readonly quotaRefusal = computed(() => {
    const phase = this.tracker.phase();

    return phase.kind === 'blocked' ? phase.refusal : null;
  });

  /** A deliberate new question, so the next ask takes a fresh key rather than replaying the last answer. */
  protected askAgain(): void {
    this.idempotency.clear();
    void this.ask();
  }

  protected async ask(): Promise<void> {
    if (!this.hasSubject()) return;

    const config = this.config();
    const requestId = await this.tracker.submit(() =>
      this.concepts.request(
        this.workspaceSlug(),
        {
          channelKey: config.channelKey,
          creatorConcept: this.brief(),
          sceneOverrides: config.scene,
          styleOverrides: config.style,
        },
        this.idempotency.next(),
      ),
    );

    if (requestId === null) {
      // A reused key is the one refusal a fresh key fixes; every other failure keeps it so a retry replays.
      if (this.tracker.phase().kind === 'key_reused') this.idempotency.clear();
      return;
    }

    this.idempotency.clear();
    // The pick goes with the request it came from — a concept id only resolves against its own proposal — and so
    // does any prompt composed for it.
    this.changed.emit({ ...this.clearedForNewPick(), conceptRequestId: requestId, chosen: null });
    this.announced.emit('Looking for some looks to choose from.');
  }

  /**
   * The prompt state with anything that belonged to the previous pick removed.
   *
   * A composed prompt was written for one shot of one look, so it cannot follow the creator to another. What they
   * typed themselves does follow: those are their words, and nothing here may throw them away. Wording taken from
   * a reference also stays — it came from a photograph rather than from the shot.
   */
  private clearedForNewPick(): ContentPipelinePromptState {
    const prompt = this.prompt();
    const composed = prompt.promptSource === 'composed';

    return {
      ...prompt,
      promptRequestId: null,
      generated: null,
      finalPrompt: composed ? '' : prompt.finalPrompt,
      promptSource: composed ? 'none' : prompt.promptSource,
    };
  }

  protected pick(values: readonly string[]): void {
    const value = values[0];
    const requestId = this.tracker.requestId();
    if (value === undefined || requestId === null) return;

    const [conceptId, shotKind] = value.split(PICK_SEPARATOR);
    const concept = this.conceptList().find((candidate) => candidate.conceptId === conceptId);
    const shot = concept?.shots.find((candidate) => candidate.kind === shotKind);
    if (concept === undefined || shot === undefined) return;

    const chosen = {
      conceptRequestId: requestId,
      conceptId: concept.conceptId,
      label: concept.label,
      shotKind: shot.kind,
    };
    const current = this.prompt().chosen;
    const sameShot =
      current !== null && current.conceptId === chosen.conceptId && current.shotKind === chosen.shotKind;

    this.changed.emit({ ...(sameShot ? this.prompt() : this.clearedForNewPick()), chosen });
    this.announced.emit(`Picked ${concept.label}, ${PHOTOGRAPHY_SHOT_KIND_LABELS[shot.kind]}.`);
  }

  protected stopChecking(): void {
    this.tracker.stopChecking();
  }

  protected resume(): void {
    const requestId = this.tracker.requestId();
    if (requestId !== null) this.tracker.resume(requestId);
  }
}
