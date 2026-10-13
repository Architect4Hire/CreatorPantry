import { DestroyRef, Signal, computed, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { AiOperationTracker } from '../../features/ai/ai-operation-tracker';
import { IdempotencyKey } from '../../features/content-pipeline/content-pipeline-idempotency';
import { ContentChannel } from '../../models/brand-profile.models';
import { CreativeContextPicture } from '../../models/creative-context.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { CreativeContextService } from '../../services/creative-context.service';
import { ReferenceImageService } from '../../services/reference-image.service';

// What a screen that hosts `cp-channel-post-run` has to read for it, written once for the two that do (the
// Content Pipeline's posts step and Social Studio): the channel list with the workspace's usual channels, and
// the work's pictures with the one request a creator can make about them. Neither class renders anything —
// the run does — and each is built in its host's constructor, where it may inject and register effects.

/** How the channel list and the workspace's usual channels loaded. One state, because they load together. */
export type ChannelPostChannelsState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly channels: readonly ContentChannel[]; readonly defaults: readonly string[] }
  | { readonly status: 'unavailable' };

/**
 * The channel list and the workspace's usual channels, for whichever workspace the host is showing.
 *
 * The profile is optional: a workspace with none still writes posts, it just starts the chooser empty. The
 * list is not — without it a new channel cannot be named — so only its failure is reported. **A failed read
 * does not stop a host**: posts already written are still readable and editable under the keys they have.
 */
export class ChannelPostChannels {
  private readonly profiles = inject(BrandProfileService);

  readonly state = signal<ChannelPostChannelsState>({ status: 'loading' });

  readonly list = computed<readonly ContentChannel[]>(() => {
    const state = this.state();
    return state.status === 'ready' ? state.channels : [];
  });

  readonly defaults = computed<readonly string[]>(() => {
    const state = this.state();
    return state.status === 'ready' ? state.defaults : [];
  });

  /** The workspace the list and defaults were read for, so the same one is not read twice. */
  private loadedSlug: string | null = null;

  constructor(workspaceSlug: Signal<string>) {
    // An effect rather than the constructor body: a required input has no value there, and keying it on the
    // slug makes arriving and a change of workspace one path — the second workspace's usual channels are its own.
    effect(() => {
      const slug = workspaceSlug();
      if (slug === '' || slug === this.loadedSlug) return;

      this.loadedSlug = slug;
      untracked(() => void this.load(slug));
    });
  }

  private async load(slug: string): Promise<void> {
    this.state.set({ status: 'loading' });

    const [list, profile] = await Promise.all([
      this.profiles.listContentChannels(),
      this.profiles.getBrandProfile(slug),
    ]);

    // The workspace moved on while this was being read; a later read owns the answer.
    if (slug !== this.loadedSlug) return;

    if (list.status !== 'found') {
      this.state.set({ status: 'unavailable' });
      return;
    }

    this.state.set({
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

/**
 * The pictures one piece of work names, and having one of them read so posts can be written to it (AF.6.6).
 *
 * **Nothing is written to a post request.** A reading is kept with the picture, and the package the capability
 * is grounded on picks it up from there — so asking for one is only ever asking to know more about the picture
 * before writing. The creator sees what it says first, which is what the run's picture panel shows.
 *
 * **No note is sent**, and that is load-bearing: a reading steered by a creator's note stays with its own
 * proposal and is not kept with the picture, so a note here would spend the allowance and store nothing.
 */
export class ChannelPostPictures {
  private readonly contexts = inject(CreativeContextService);
  private readonly readings = inject(ReferenceImageService);

  /** The work's pictures, as the server last described them. */
  readonly pictures = signal<readonly CreativeContextPicture[]>([]);

  /** The picture a reading is in flight for, by reference id, or null. */
  readonly readingPictureId = signal<string | null>(null);

  /** What the last attempt said, by the reference id of the picture it was about. */
  readonly readingProblems = signal<Readonly<Record<string, string>>>({});

  /** The reading in flight. One at a time: a creator reads the picture these posts go with, not a library. */
  private readonly tracker = new AiOperationTracker((requestId) =>
    this.readings.watch(this.workspaceSlug(), requestId),
  );

  private readonly idempotency = new IdempotencyKey();

  /** The work whose pictures were last read, so the same ones are not read twice. */
  private picturesKey: string | null = null;

  private gone = false;

  /**
   * @param revision Anything that changes when the work's pictures may have: a host that lets a creator choose
   * or remove one passes a value derived from the work's references, so the list is read again when they do.
   */
  constructor(
    private readonly workspaceSlug: Signal<string>,
    private readonly creativeContextId: Signal<string | null>,
    private readonly announce: (sentence: string) => void,
    revision: Signal<string> = signal(''),
  ) {
    // The work's pictures, for whichever workspace and work the host now names.
    effect(() => {
      const slug = workspaceSlug();
      const contextId = creativeContextId();
      const key = `${slug}|${contextId ?? ''}|${revision()}`;
      if (key === this.picturesKey) return;

      this.picturesKey = key;
      untracked(() => void this.load(slug, contextId));
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
          void this.load(this.workspaceSlug(), this.creativeContextId());
          this.announce('The picture has been read. What it says is shown with it.');
          return;
        }

        this.report(readingFor, phase.kind);
      });
    });

    inject(DestroyRef).onDestroy(() => {
      this.gone = true;
      this.tracker.destroy();
    });
  }

  /** Have one picture read. */
  async read(picture: CreativeContextPicture): Promise<void> {
    if (this.readingPictureId() !== null) return;

    this.clearProblem(picture.referenceId);
    this.readingPictureId.set(picture.referenceId);

    const requested = await this.tracker.submit(() =>
      this.readings.request(
        this.workspaceSlug(),
        { picture: ChannelPostPictures.asSource(picture), note: '' },
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

  /** Which picture to read, in the terms the reading route names one. */
  private static asSource(picture: CreativeContextPicture) {
    return picture.generatedImageId !== null
      ? ({ source: 'GeneratedImage', generatedImageId: picture.generatedImageId } as const)
      : ({
          source: 'DamAsset',
          mediaAssetId: picture.mediaAssetId!,
          mediaAssetVersionNumber: picture.mediaAssetVersionNumber,
        } as const);
  }

  /** Say what went wrong, against the picture it was about, and announce it once. */
  private report(referenceId: string, phase: string): void {
    const sentence = READING_PROBLEMS[phase] ?? READING_PROBLEMS['unavailable'];

    this.readingProblems.update((current) => ({ ...current, [referenceId]: sentence }));
    this.announce(sentence);
  }

  private clearProblem(referenceId: string): void {
    this.readingProblems.update((current) => {
      const { [referenceId]: _gone, ...rest } = current;

      return rest;
    });
  }

  /** Read the work's pictures again, so what is on screen is what the server holds. */
  private async load(slug: string, contextId: string | null): Promise<void> {
    if (contextId === null) {
      this.pictures.set([]);
      return;
    }

    const outcome = await firstValueFrom(this.contexts.pictures(slug, contextId));

    // The host moved on to another workspace or piece of work while this was being read.
    if (this.gone || this.workspaceSlug() !== slug || this.creativeContextId() !== contextId) return;

    // A failed read leaves whatever is on screen: the posts do not depend on it, and blanking the picture
    // would make a dropped response look like a work with no picture in it.
    if (outcome.status === 'found') this.pictures.set(outcome.pictures);
  }
}
