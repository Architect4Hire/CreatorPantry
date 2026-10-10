import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CpNoticeComponent } from '@creator-pantry/ui';

import { BrandProfileService } from '../../services/brand-profile.service';
import { AiUsageService } from '../../services/ai-usage.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { CreativeContextPicture } from '../../models/creative-context.models';
import { CreativeContextService } from '../../services/creative-context.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { AiOperationTracker } from '../ai/ai-operation-tracker';
import { ContentPipelineDraft, ContentPipelinePostsState } from '../../models/content-pipeline.models';
import { CreativeContextSession } from '../../services/creative-context-session';
import { ChannelPostRunComponent, ChannelPostRunState } from '../../shared/channel-posts/channel-post-run.component';
import { IdempotencyKey } from './content-pipeline-idempotency';

/** How the channel list and the workspace's usual channels loaded. One state, because they load together. */
type ChannelsState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly channels: readonly ContentChannel[]; readonly defaults: readonly string[] }
  | { readonly status: 'unavailable' };

/**
 * Step 5 of the Content Pipeline: the words that go out with the pictures (AF.6.5, fixing FLU-001).
 *
 * **The work is {@link ChannelPostRunComponent}'s**, which master 13.3's Social Studio will share: choosing
 * channels, asking, and reviewing each post on its own are the same job on both screens, so they are written
 * once. What is this step's own is where the run's inputs come from — the piece of work from the creative
 * context this run is on, the channel list and the workspace's usual channels from the brand profile, the
 * allowance from the account — and where its kept state goes.
 *
 * **The posts belong to the piece of work, not to this device.** What this step keeps is the request it is
 * waiting on and the words the creator has not saved yet; everything else is read from the work, so the same
 * run opens the same on another device.
 *
 * **A failed channel read does not stop the step.** The posts are still readable and editable — the list is
 * only how a channel gets its name and how a new one is offered, so without it the run says so and carries on
 * with the keys it has.
 */
@Component({
  selector: 'cp-content-pipeline-posts-step',
  standalone: true,
  imports: [ChannelPostRunComponent, CpNoticeComponent],
  template: `
    @if (channels().status === 'unavailable') {
      <cp-notice tone="warning" role="status">
        The list of channels couldn't be loaded, so new channels can't be offered right now. Posts already
        written are still here, and still yours to edit.
      </cp-notice>
    }

    <cp-channel-post-run
      [workspaceSlug]="workspaceSlug()"
      [creativeContextId]="creativeContextId()"
      [state]="runState()"
      [channels]="channelList()"
      [channelDefaults]="channelDefaults()"
      [pictures]="pictures()"
      [readingPictureId]="readingPictureId()"
      [readingProblems]="readingProblems()"
      [allowance]="allowance()"
      (changed)="onRun($event)"
      (pictureReadRequested)="onReadPicture($event)"
      (announced)="announced.emit($event)"
    />
  `,
  styles: [':host { display: block; }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelinePostsStepComponent {
  private readonly profiles = inject(BrandProfileService);
  private readonly contexts = inject(CreativeContextService);
  private readonly readings = inject(ReferenceImageService);
  private readonly usage = inject(AiUsageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  /** The work's hold on its creative context, which is what the posts are about. */
  readonly session = input.required<CreativeContextSession>();

  readonly changed = output<ContentPipelineDraft>();
  readonly announced = output<string>();

  protected readonly allowance = this.usage.allowance;

  protected readonly channels = signal<ChannelsState>({ status: 'loading' });

  /** The piece of work, or null until this run has been filed on one. */
  protected readonly creativeContextId = computed(() => this.session().context()?.id ?? null);

  protected readonly channelList = computed<readonly ContentChannel[]>(() => {
    const state = this.channels();
    return state.status === 'ready' ? state.channels : [];
  });

  protected readonly channelDefaults = computed<readonly string[]>(() => {
    const state = this.channels();
    if (state.status !== 'ready') return [];

    // The workspace's usual channels, or the one this run was set up for when the profile names none. A
    // creator who said "this is for Instagram" on the first step should not have to say it again here.
    if (state.defaults.length > 0) return state.defaults;

    const chosen = this.draft().config.channelKey;

    return chosen === null ? [] : [chosen];
  });

  protected readonly runState = computed<ChannelPostRunState>(() => this.draft().posts);

  /** The work's pictures, as the server last described them. */
  protected readonly pictures = signal<readonly CreativeContextPicture[]>([]);

  /** The picture a reading is in flight for, by reference id, or null. */
  protected readonly readingPictureId = signal<string | null>(null);

  /** What the last attempt said, by the reference id of the picture it was about. */
  protected readonly readingProblems = signal<Readonly<Record<string, string>>>({});

  /** The reading in flight. One at a time: a creator reads the picture these posts go with, not a library. */
  private readonly tracker = new AiOperationTracker((requestId) =>
    this.readings.watch(this.workspaceSlug(), requestId),
  );

  private readonly idempotency = new IdempotencyKey();

  /** The work whose pictures were last read, so the same ones are not read twice. */
  private picturesKey: string | null = null;

  private gone = false;

  /** The workspace the channel list and defaults were read for, so the same one is not read twice. */
  private loadedSlug: string | null = null;

  constructor() {
    // An effect rather than the constructor: a required input has no value there, and keying it on the slug
    // makes arriving and a change of workspace one path — the second workspace's usual channels are its own.
    effect(() => {
      const slug = this.workspaceSlug();
      if (slug === this.loadedSlug) return;

      this.loadedSlug = slug;
      untracked(() => void this.load(slug));
    });

    // The work's pictures, for whichever workspace and work the inputs now name.
    effect(() => {
      const slug = this.workspaceSlug();
      const contextId = this.creativeContextId();
      const key = `${slug}|${contextId ?? ''}`;
      if (key === this.picturesKey) return;

      this.picturesKey = key;
      untracked(() => void this.loadPictures(slug, contextId));
    });

    // A finished reading is kept with the picture by the server, so the pictures are read again rather than
    // patched here — and the creator sees what it says before any post is written.
    effect(() => {
      const phase = this.tracker.phase();
      if (phase.kind === 'submitting' || phase.kind === 'watching') return;

      untracked(() => {
        const readingFor = this.readingPictureId();
        if (readingFor === null) return;

        this.readingPictureId.set(null);

        if (phase.kind === 'ready') {
          void this.loadPictures(this.workspaceSlug(), this.creativeContextId());
          this.announced.emit('The picture has been read. What it says is on the step.');
          return;
        }

        this.report(readingFor, phase.kind);
      });
    });

    this.destroyRef.onDestroy(() => {
      this.gone = true;
      this.tracker.destroy();
    });
  }

  protected onRun(posts: ContentPipelinePostsState): void {
    this.changed.emit({ ...this.draft(), posts });
  }

  /**
   * Have one picture read, so the posts can be written to it (AF.6.6).
   *
   * **Nothing is written to the post request.** The reading is kept with the picture, and the package the
   * capability is grounded on picks it up from there — so asking for one is only ever asking to know more
   * about the picture before writing. The creator sees what it says first, which is what the panel shows.
   *
   * **No note is sent**, and that is load-bearing: a reading steered by a creator's note stays with its own
   * proposal and is not kept with the picture, so a note here would spend the allowance and store nothing.
   */
  protected async onReadPicture(picture: CreativeContextPicture): Promise<void> {
    if (this.readingPictureId() !== null) return;

    this.clearProblem(picture.referenceId);
    this.readingPictureId.set(picture.referenceId);

    const requested = await this.tracker.submit(() =>
      this.readings.request(
        this.workspaceSlug(),
        { picture: this.asSource(picture), note: '' },
        this.idempotency.next(),
      ),
    );

    if (this.gone) return;

    if (requested === null) {
      // The key is deliberately kept: a failed ask retried with the same one is a retry rather than a second
      // reading, which is what IdempotencyKey exists for.
      this.readingPictureId.set(null);
      this.report(picture.referenceId, this.tracker.phase().kind);
      return;
    }

    // Accepted, so the next ask is a different question and gets a key of its own.
    this.idempotency.clear();
  }

  /** Say what went wrong, against the picture it was about, and announce it once. */
  private report(referenceId: string, phase: string): void {
    const sentence = READING_PROBLEMS[phase] ?? READING_PROBLEMS['unavailable'];

    this.readingProblems.update((current) => ({ ...current, [referenceId]: sentence }));
    this.announced.emit(sentence);
  }

  private clearProblem(referenceId: string): void {
    this.readingProblems.update((current) => {
      const { [referenceId]: _gone, ...rest } = current;

      return rest;
    });
  }

  /** Which picture to read, in the terms the reading route names one. */
  private asSource(picture: CreativeContextPicture) {
    return picture.generatedImageId !== null
      ? ({ source: 'GeneratedImage', generatedImageId: picture.generatedImageId } as const)
      : ({
          source: 'DamAsset',
          mediaAssetId: picture.mediaAssetId!,
          mediaAssetVersionNumber: picture.mediaAssetVersionNumber,
        } as const);
  }

  /** Read the work's pictures again, so what is on screen is what the server holds. */
  private async loadPictures(slug: string, contextId: string | null): Promise<void> {
    if (contextId === null) {
      this.pictures.set([]);
      return;
    }

    const outcome = await firstValueFrom(this.contexts.pictures(slug, contextId));

    if (this.gone) return;

    // A failed read leaves whatever is on screen: the posts do not depend on it, and blanking the picture
    // would make a dropped response look like a work with no picture in it.
    if (outcome.status === 'found') this.pictures.set(outcome.pictures);
  }

  /**
   * The channel list and the workspace's usual channels.
   *
   * The profile is optional: a workspace with none still writes posts, it just starts the chooser empty. The
   * list is not — without it a new channel cannot be named — so only its failure is reported.
   */
  private async load(slug: string): Promise<void> {
    this.channels.set({ status: 'loading' });

    const [list, profile] = await Promise.all([
      this.profiles.listContentChannels(),
      this.profiles.getBrandProfile(slug),
    ]);

    if (list.status !== 'found') {
      this.channels.set({ status: 'unavailable' });
      return;
    }

    this.channels.set({
      status: 'ready',
      channels: list.channels,
      defaults: profile.status === 'found' ? profile.profile.channelDefaults : [],
    });
  }
}

/** What a refused reading says, by the phase the tracker reached. One sentence each, with its own remedy. */
const READING_PROBLEMS: Readonly<Record<string, string>> = {
  not_enabled: 'Reading a picture is not switched on for this workspace yet.',
  forbidden: 'You do not have permission to have a picture read in this workspace.',
  blocked: 'Your AI allowance will not cover reading a picture right now.',
  key_reused: 'That did not go through. Asking again is safe.',
  refused: 'That picture could not be read. It may have been removed or be a kind we cannot read.',
  gone: 'That reading is no longer there. Asking again is the way on.',
  failed: 'The picture could not be read. The posts will know only that a picture goes with them.',
  unavailable: 'The picture could not be read just now. Trying again is safe.',
};
