import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CpNoticeComponent } from '@creator-pantry/ui';

import { ContentPipelineDraft, ContentPipelineImagesState } from '../../models/content-pipeline.models';
import { keeperReferencesOf } from '../../models/creative-context.models';
import { avoidTextFor } from '../../models/generated-image.models';
import { CreativeContextReferenceOutcome, CreativeContextSession } from '../../services/creative-context-session';
import { GeneratedImageRunComponent } from './generated-image-run.component';

/** What stopped a mark from being written, in the creator's terms. Every outcome has a sentence. */
const MARK_FAILURES: Readonly<Record<Exclude<CreativeContextReferenceOutcome, 'saved' | 'duplicate'>, string>> = {
  source_unavailable: 'That picture can no longer be kept. It may have been declined, or held past the time pictures are kept for.',
  limit: 'This piece of work already names as many sources as it can. Remove one to keep another picture.',
  forbidden: 'You do not have permission to change this piece of work.',
  held: 'That could not be done yet. Sort out the notice about your unsaved changes below, then try again.',
  unavailable: "That couldn't be done right now. Nothing was changed.",
};

/**
 * Step 4 of the Content Pipeline: from a prompt to the pictures worth keeping (PIPE-UI-004).
 *
 * **The work is `GeneratedImageRunComponent`'s**, which the Image Studio shares: asking, watching, choosing,
 * declining and tidying up are the same on both screens, so they are written once. What is this step's own is
 * where a run's three inputs come from — the prompt the previous step settled, the avoid list that came with
 * its composition, and the count from the first step — and where its answers go.
 *
 * **A keeper is a reference on the work, not a note on this device** (AF.4.3). Marking one writes a
 * `Keeper` picture reference to the creative context and unmarking removes it, so the library step reads the
 * creator's choice from the server rather than from a browser, and the same work opens the same on another
 * device. The run id stays on the device, where it has always been: it is this screen's own bookmark.
 *
 * **A mark is one write, and it either happened or it did not.** The run renders what the context says, so a
 * write that fails leaves the box where it was and says why, rather than showing a mark nothing recorded.
 */
@Component({
  selector: 'cp-content-pipeline-images-step',
  standalone: true,
  imports: [CpNoticeComponent, GeneratedImageRunComponent],
  template: `
    <cp-generated-image-run
      [workspaceSlug]="workspaceSlug()"
      [promptText]="promptText()"
      [avoidText]="avoidText()"
      [variantCount]="draft().config.variantCount"
      [state]="draft().images"
      [keepers]="keepers()"
      (changed)="onImages($event)"
      (keepToggled)="onKeepToggled($event)"
      (announced)="announced.emit($event)"
    />

    @if (problem(); as sentence) {
      <cp-notice tone="error" role="alert">{{ sentence }}</cp-notice>
    }
  `,
  styles: [':host { display: block; }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineImagesStepComponent {
  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  /** The work's hold on its creative context, where a keeper is recorded. */
  readonly session = input.required<CreativeContextSession>();
  readonly changed = output<ContentPipelineDraft>();
  readonly announced = output<string>();

  /** What stopped the last mark from being written. Cleared when the creator tries anything else. */
  protected readonly problem = signal('');

  /** The prompt as it will be sent: the creator's own words, which is what the previous step settled. */
  protected readonly promptText = computed(() => this.draft().prompt.finalPrompt);

  /**
   * What the request will tell the provider to avoid, or null.
   *
   * It came with the composition the creator was offered, so it is a choice they already saw.
   */
  protected readonly avoidText = computed(() => avoidTextFor(this.draft().prompt.generated?.avoid ?? []));

  /**
   * The pictures of this run the work names as keepers.
   *
   * Read from the context every time, so what is on screen is what is recorded. A keeper from an earlier run
   * is left where it is and simply not shown: this step is about the run in front of the creator, and the
   * library step is where every keeper of the work is accounted for.
   */
  protected readonly keepers = computed<readonly string[]>(() =>
    keeperReferencesOf(this.session().context())
      .map((reference) => reference.generatedImageId)
      .filter((id): id is string => id !== null),
  );

  protected onImages(images: ContentPipelineImagesState): void {
    this.changed.emit({ ...this.draft(), images });
  }

  protected async onKeepToggled(change: { readonly id: string; readonly keep: boolean }): Promise<void> {
    this.problem.set('');

    const session = this.session();
    const outcome = change.keep
      ? await session.addReference({ kind: 'GeneratedImage', purpose: 'Keeper', generatedImageId: change.id })
      : await this.release(change.id);

    if (outcome === 'saved' || outcome === 'duplicate') {
      this.announced.emit(change.keep ? 'Kept.' : 'No longer kept.');
      return;
    }

    this.problem.set(MARK_FAILURES[outcome]);
    this.announced.emit(MARK_FAILURES[outcome]);
  }

  /** Stop the work naming a picture as a keeper. Already gone is the outcome the creator asked for. */
  private async release(generatedImageId: string): Promise<CreativeContextReferenceOutcome> {
    const session = this.session();
    const reference = keeperReferencesOf(session.context()).find(
      (each) => each.generatedImageId === generatedImageId,
    );

    return reference === undefined ? 'saved' : session.removeReference(reference.id);
  }
}
