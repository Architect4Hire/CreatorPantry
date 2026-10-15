import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpNoticeComponent } from '@creator-pantry/ui';

import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';
import { BrandProfileService } from '../../../services/brand-profile.service';
import { HANDOFF_ROUTES } from '../../../shared/use-this-in/handoff-destinations';
import { workflowRunQuery } from '../../../shared/workflow-shell/workflow-return';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { ImageStudioComponent } from '../../image-studio/image-studio.component';
import { ImageStudioHost, ImageStudioPart } from '../../image-studio/image-studio-host';
import { contextPointer, newestLinked, runContextId } from '../creative-work/creative-work-run';

/**
 * The first three steps of "Create food images" (13A.19) — what the picture is for, planning it, and making and
 * keeping pictures — each one part of the Image Studio, hosted ({@link ImageStudioHost}).
 *
 * <p><strong>Orchestration only.</strong> Every field, request, cost notice and save is the studio's own panels',
 * so a picture made here and one made in the studio are the same work under the same piece of work. The step
 * names the work to the studio, keeps it on the run, and tells the shell what the step made.</p>
 *
 * <p><strong>What is kept where.</strong> What the work is about is saved to the workspace as it is answered. The
 * prompt, the picks and the requests being watched are kept on this device under the piece of work, as the studio
 * keeps them; another device opens the same work with its prompt to write again. Pictures saved to the library are
 * the workspace's.</p>
 *
 * <p><strong>Nothing cancels a run of pictures,</strong> because no route does: "Stop checking" stops this page
 * asking, and the run carries on on the server, where coming back finds it.</p>
 */
@Component({
  selector: 'cp-images-studio-step',
  standalone: true,
  imports: [RouterLink, CpNoticeComponent, ImageStudioComponent],
  providers: [ImageStudioHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (configured()) {
      @if (missingWork()) {
        <cp-notice role="note">
          This step works on the picture you chose in the first step, which isn’t there yet. Go back to the first
          step to choose what the picture is for.
        </cp-notice>
      } @else {
        <cp-image-studio />

        @if (keepFailed()) {
          <cp-notice tone="warning" role="status">
            Your work is saved, but this workflow couldn’t be pointed at it just now, so coming back may not find it
            here. It is still in the Image Studio.
          </cp-notice>
        }

        @if (contextId(); as id) {
          <p class="cp-muted elsewhere">
            <a [routerLink]="studioLink(id)" [queryParams]="returnQuery()">Open this in the Image Studio</a>
            to see every part of it on one page.
          </p>
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .elsewhere {
      margin: 0;
    }
  `,
})
export class ImagesStudioStepComponent implements OnInit {
  readonly part = input.required<ImageStudioPart>();
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly host = inject(ImageStudioHost);
  private readonly port = inject(WorkflowStepPort);
  private readonly brandProfiles = inject(BrandProfileService);
  private readonly studio = viewChild(ImageStudioComponent);

  /** False until the host is set, so the studio reads it once, set. */
  protected readonly configured = signal(false);
  protected readonly contextId = signal<string | null>(null);
  protected readonly keepFailed = signal(false);

  /** A later part with no work to show: the first step's, which the run does not hold. */
  protected readonly missingWork = computed(() => this.part() !== 'about' && this.contextId() === null);

  protected readonly returnQuery = computed(() => workflowRunQuery(this.run().id));

  /** The pointer on its way to the run, which a move waits for. */
  private keeping: Promise<boolean> = Promise.resolve(true);
  private gone = false;

  /** The pictures this work saved to the library, as the run names what a step made. */
  private readonly kept = computed<readonly WorkflowResourcePointer[]>(() =>
    (this.studio()?.workContext()?.references ?? [])
      .filter((reference) => reference.kind === 'DamAsset' && reference.purpose === 'Keeper' && reference.mediaAssetId !== null)
      .map((reference) => ({ kind: 'MediaAsset', id: reference.mediaAssetId! })),
  );

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    const unregister = this.port.register({
      // Every answer is kept as it is given; what is still on its way is sent before a move.
      unsaved: signal(false).asReadonly(),
      saving: computed(() => this.studio()?.sending() ?? false),
      flush: () => this.flush(),
      // Only the first part keeps its work in a draft: the later parts' actions take none, and the run already
      // links the piece of work the first part made.
      draft: computed(() => {
        const id = this.contextId();
        return id === null || this.part() !== 'about' ? null : contextPointer(id);
      }),
      results: computed(() => {
        const id = this.contextId();
        switch (this.part()) {
          case 'about':
            return id === null ? [] : [contextPointer(id)];
          case 'make':
            return this.kept();
          default:
            return [];
        }
      }),
      ready: computed(() => {
        switch (this.part()) {
          case 'about':
            return this.contextId() !== null;
          case 'make':
            return this.kept().length > 0;
          default:
            return true;
        }
      }),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    const run = this.run();
    const contextId = runContextId(run);

    this.contextId.set(contextId);
    this.host.part = this.part();
    this.host.contextId = contextId;
    // Offered on the first step only, and only offered: the run's recipe is linked when the creator chooses it.
    this.host.suggestedRecipeId = this.part() === 'about' ? newestLinked(run, 'Recipe') : null;
    this.host.filed = (id) => this.filed(id);
    void this.readBrand(this.workspaceSlug());

    this.configured.set(true);
  }

  protected studioLink(contextId: string): readonly string[] {
    return ['/', this.workspaceSlug(), ...HANDOFF_ROUTES.imageStudio(contextId)];
  }

  private filed(contextId: string): void {
    this.contextId.set(contextId);
    this.keepFailed.set(false);
    this.keeping = this.port.keepDraft(contextPointer(contextId)).then((kept) => {
      if (!this.gone) this.keepFailed.set(!kept);
      return kept;
    });
  }

  private async flush(): Promise<boolean> {
    const sent = (await this.studio()?.flushWork()) ?? true;
    await this.keeping;
    return sent;
  }

  /** The brand's look and picture count. Never a reason to stop: no profile, or none readable, is said and passed. */
  private async readBrand(slug: string): Promise<void> {
    const outcome = await this.brandProfiles.getBrandProfile(slug);
    if (this.gone) return;

    this.host.brand.set(
      outcome.status === 'found'
        ? { status: 'ready', channelKeys: outcome.profile.channelDefaults, pictureCount: outcome.profile.defaultPictureCount }
        : { status: outcome.status === 'first_use' ? 'none' : 'unavailable', channelKeys: [], pictureCount: null },
    );
  }
}
