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
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { AiOperationTracker } from '../../features/ai/ai-operation-tracker';
import { ContentChannel } from '../../models/brand-profile.models';
import { CreativeContextPicture } from '../../models/creative-context.models';
import {
  ChannelPostChannel,
  ChannelPostDecision,
  ChannelPostPackage,
  channelPostCopyText,
} from '../../models/channel-post.models';
import { ClipboardService } from '../../core/clipboard.service';
import { AiAllowanceState } from '../../services/ai-usage.service';
import { ChannelPostService, ChannelPostWriteOutcome } from '../../services/channel-post.service';
import { AiAllowanceNoticeComponent } from '../ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../allowance-gate';
import { ChannelPostPictureComponent } from './channel-post-picture.component';
import {
  ChannelPostBusy,
  ChannelPostIntent,
  ChannelPostReviewComponent,
  IDLE_CHANNEL_POST_BUSY,
} from './channel-post-review.component';

/** What this surface keeps between visits: the last request, and words nobody has saved yet. */
export interface ChannelPostRunState {
  /** The AF.6.4 request last asked for, so its posts are found again after a refresh. */
  readonly requestId: string | null;
  /** The channels that request named, so a resumed run knows which cards are still being written. */
  readonly requestedChannelKeys: readonly string[];
  /**
   * The creator's unsaved words, by channel key.
   *
   * **The reason an edit survives anything.** It is kept here rather than inside the card, so a failed save,
   * a neighbouring channel being written again, and leaving the step all leave the words exactly as they
   * were. Cleared for a channel only when its save lands.
   */
  readonly unsaved: Readonly<Record<string, string>>;
}

export function emptyChannelPostRunState(): ChannelPostRunState {
  return { requestId: null, requestedChannelKeys: [], unsaved: {} };
}

/** The sentences that differ between the surfaces this run appears on. */
export interface ChannelPostRunWording {
  /** Under the first section's heading: what asking for posts here means. */
  readonly chooseIntro: string;
  readonly forbidden: string;
  /** The second section's heading, over the cards. */
  readonly reviewHeading: string;
  readonly reviewIntro: string;
}

/** The Content Pipeline's wording, and the default — it was the first caller. */
export const PIPELINE_CHANNEL_POST_WORDING: ChannelPostRunWording = {
  chooseIntro:
    'Pick where these words are going. Your usual channels are already ticked; add or drop any of them. Each channel gets its own post, written to that channel’s own length.',
  forbidden:
    'You do not have permission to write posts in this workspace. Ask an Owner or Editor to run this step.',
  reviewHeading: 'Read them through',
  reviewIntro:
    'One post per channel, each on its own. Nothing goes out from here, and nothing counts as yours until you accept it.',
};

/** One channel, ready to render: what the server says about it, and what a creator calls it. */
interface ChannelCard {
  readonly channel: ChannelPostChannel;
  readonly name: string;
  readonly busy: ChannelPostBusy;
  readonly copied: boolean;
}

/**
 * Writing and reviewing one piece of work's posts, channel by channel (AF.6.5).
 *
 * **One composition for two screens**, which is why it lives in `shared/`: the Content Pipeline's posts step
 * is the first caller and master 13.3's Social Studio is the second. Everything either of them may reword is
 * in {@link ChannelPostRunWording}, so nothing else about reviewing a post can drift between them.
 *
 * **Two sections in the order the work happens** — choosing channels and asking, then reading the posts
 * through. The second appears once there is something in it.
 *
 * **Controlled.** It renders the state handed in and emits the next one, so the caller stays the single
 * source of truth and an unsaved edit is kept wherever the caller keeps its work.
 *
 * **The package is the server's, read back after every write.** Each edit and decision answers with the whole
 * package and that answer replaces what is on screen — so a card never shows a revision the server has moved
 * past, and "accepting twice" is the server's own answer rather than this screen guessing at one.
 *
 * **Regenerating names one channel.** It is a fresh request carrying that key alone, so a neighbour's words,
 * its decision and its unsaved edit are all untouched — the contract makes that true and the tracker here
 * only has to not undo it.
 */
@Component({
  selector: 'cp-channel-post-run',
  standalone: true,
  imports: [
    AiAllowanceNoticeComponent,
    ChannelPostPictureComponent,
    ChannelPostReviewComponent,
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
  ],
  templateUrl: './channel-post-run.component.html',
  styleUrl: './channel-post-run.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChannelPostRunComponent {
  private readonly service = inject(ChannelPostService);
  private readonly clipboard = inject(ClipboardService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  /** The piece of work the posts are about. Null before one exists, which holds the whole step. */
  readonly creativeContextId = input.required<string | null>();
  readonly state = input.required<ChannelPostRunState>();
  /** Every channel the product knows, active and retired, for naming and for offering. */
  readonly channels = input.required<readonly ContentChannel[]>();
  /** The workspace's usual channels, which the chooser starts ticked. */
  readonly channelDefaults = input<readonly string[]>([]);
  /**
   * The pictures this piece of work names, kept or chosen (AF.6.6).
   *
   * Read by the caller, because reading them is a workspace read like any other and the caller is where the
   * work's own state lives. An empty list is a piece of work with no picture, which is the ordinary case.
   */
  readonly pictures = input<readonly CreativeContextPicture[]>([]);
  /** The picture a reading is being asked for, by reference id, or null. */
  readonly readingPictureId = input<string | null>(null);
  /**
   * What the last attempt to have a picture read said, by the reference id of the picture it was about.
   *
   * Keyed by picture rather than by whichever read is in flight, because the two are not the same picture:
   * a refusal is reported after the attempt has ended, and a problem the panel could not show would be a
   * creator pressing a button and being told nothing.
   */
  readonly readingProblems = input<Readonly<Record<string, string>>>({});
  readonly allowance = input.required<AiAllowanceState>();
  readonly wording = input<ChannelPostRunWording>(PIPELINE_CHANNEL_POST_WORDING);
  readonly sectionIdPrefix = input('cp-channel-posts');

  readonly changed = output<ChannelPostRunState>();
  /** The creator asked for one picture to be read, so the posts can be written to it. */
  readonly pictureReadRequested = output<CreativeContextPicture>();
  readonly announced = output<string>();

  protected readonly tracker = new AiOperationTracker((requestId) =>
    this.service.watch(this.workspaceSlug(), requestId),
  );

  /** The package as the server last described it, or null for a piece of work with no posts yet. */
  private readonly packageSignal = signal<ChannelPostPackage | null>(null);

  /** How the first read of the package went. Only the first: later reads replace the package in place. */
  protected readonly loading = signal(true);
  protected readonly loadFailed = signal(false);

  /** The channels ticked in the chooser. Seeded from the workspace's defaults and then the creator's. */
  protected readonly chosen = signal<readonly string[]>([]);

  /** True while the chooser is open on a piece of work that already has posts. */
  protected readonly choosing = signal(false);

  /** What is in flight per channel, and what the last write said about it. */
  private readonly busy = signal<ReadonlyMap<string, ChannelPostBusy>>(new Map());

  /** The channel whose words are on the clipboard, or null. One at a time, as the button says. */
  protected readonly copiedChannel = signal<string | null>(null);

  /** The channels the current request is writing, so their cards can say so while it runs. */
  private readonly writing = signal<readonly string[]>([]);

  /** What stopped the last request, in the creator's terms. Cleared when they ask for anything else. */
  protected readonly requestProblem = signal('');

  /** The workspace and work this run is reading, so the same one is not read twice. */
  private followedKey: string | null = null;

  /** True once destroyed, so nothing that was awaiting an answer reports to a screen that has gone. */
  private gone = false;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.gone = true;
      this.tracker.destroy();
    });

    // One effect for arriving, for a change of workspace and for a change of work: the package is read for
    // whichever the inputs now name. Keyed on both, so a step reused rather than rebuilt cannot show one
    // workspace's posts under another (.claude/rules/tenancy.md).
    effect(() => {
      const slug = this.workspaceSlug();
      const contextId = this.creativeContextId();
      const key = `${slug}|${contextId ?? ''}`;
      if (key === this.followedKey) return;

      this.followedKey = key;
      untracked(() => void this.load(slug, contextId));
    });

    // The chooser starts at the workspace's usual channels, and only while the creator has chosen nothing.
    effect(() => {
      const defaults = this.channelDefaults();
      if (defaults.length === 0) return;

      untracked(() => {
        if (this.chosen().length === 0 && this.packageSignal() === null) {
          this.chosen.set(this.withoutWritten(defaults));
        }
      });
    });

    // A request this run is following, picked back up after a refresh.
    effect(() => {
      const requestId = this.state().requestId;
      if (requestId === null || this.tracker.requestId() === requestId) return;

      untracked(() => {
        this.writing.set(this.state().requestedChannelKeys);
        this.tracker.resume(requestId);
      });
    });

    // The posts a finished request wrote are on the server, so the package is read again rather than
    // assembled here. Announced too: a creator who looked away is told the words have arrived.
    effect(() => {
      const phase = this.tracker.phase();
      if (phase.kind !== 'ready') return;

      untracked(() => {
        const slug = this.workspaceSlug();
        const contextId = this.creativeContextId();
        const written = this.writing();

        this.writing.set([]);
        this.changed.emit({ ...this.state(), requestId: null, requestedChannelKeys: [] });
        void this.reload(slug, contextId, () =>
          this.announced.emit(
            written.length === 1 ? 'Your post is ready to read through.' : 'Your posts are ready to read through.',
          ),
        );
      });
    });
  }

  /** Every channel with a post, newest activity first — which is the order a creator last worked in. */
  protected readonly cards = computed<readonly ChannelCard[]>(() => {
    const names = this.channelNames();
    const busy = this.busy();
    const writing = new Set(this.writing());
    const copied = this.copiedChannel();

    return (this.packageSignal()?.channels ?? []).map((channel) => ({
      channel,
      name: names.get(channel.channelKey) ?? channel.channelKey,
      busy: {
        ...(busy.get(channel.channelKey) ?? IDLE_CHANNEL_POST_BUSY),
        editing: this.state().unsaved[channel.channelKey] ?? null,
        regenerating: writing.has(channel.channelKey),
      },
      copied: copied === channel.channelKey,
    }));
  });

  /** True once this piece of work has at least one post, which is when the second section exists. */
  protected readonly hasPosts = computed(() => this.cards().length > 0);

  /** The chooser is shown before the first post, and afterwards only when the creator asks for it. */
  protected readonly showChooser = computed(() => !this.hasPosts() || this.choosing());

  /**
   * What the chooser offers: every active channel this work has no post for.
   *
   * A channel that already has one is left out rather than disabled — writing it again is the card's own
   * "Write it again", which is one control for one idea instead of two. A retired channel is never offered,
   * which is the server's rule as well: a first post cannot be written for one.
   */
  protected readonly options = computed<readonly CpChoiceOption[]>(() => {
    const written = new Set((this.packageSignal()?.channels ?? []).map((channel) => channel.channelKey));

    return this.channels()
      .filter((channel) => channel.isActive && !written.has(channel.key))
      .map((channel) => ({ value: channel.key, label: channel.displayName }));
  });

  protected readonly blocked = computed(() => allowanceBlocksNewRequests(this.allowance()));

  protected readonly canAsk = computed(
    () =>
      this.creativeContextId() !== null &&
      this.chosen().length > 0 &&
      !this.blocked() &&
      !this.tracker.busy(),
  );

  protected readonly quotaRefusal = computed(() => {
    const phase = this.tracker.phase();
    return phase.kind === 'blocked' ? phase.refusal : null;
  });

  protected onChosen(keys: readonly string[]): void {
    this.chosen.set(keys);
    this.requestProblem.set('');
  }

  /** Ask for a post on each ticked channel. */
  protected async ask(): Promise<void> {
    const keys = this.chosen();
    if (keys.length === 0) return;

    await this.send(keys);
    if (!this.gone) {
      this.chosen.set([]);
      this.choosing.set(false);
    }
  }

  /**
   * Start checking on the request again, after the loop gave up on its own.
   *
   * It asks for nothing new and spends nothing: the request is already queued, so this is the same poll
   * picked back up — which is why it is offered rather than a second "Write the posts".
   */
  protected recheck(): void {
    const requestId = this.state().requestId;
    if (requestId === null) return;

    this.tracker.resume(requestId);
  }

  protected openChooser(): void {
    this.requestProblem.set('');
    this.choosing.set(true);
  }

  protected closeChooser(): void {
    this.choosing.set(false);
  }

  /** What the creator asked for on one channel. Each one write, and each reported where it happened. */
  protected async onIntent(channelKey: string, intent: ChannelPostIntent): Promise<void> {
    switch (intent.kind) {
      case 'edit':
        this.setUnsaved(channelKey, this.showingBodyOf(channelKey));
        this.mark(channelKey, { problem: '' });
        return;
      case 'typed':
        this.setUnsaved(channelKey, intent.body);
        return;
      case 'cancel':
        this.clearUnsaved(channelKey);
        this.mark(channelKey, { problem: '' });
        this.announced.emit('Your changes were put back.');
        return;
      case 'save':
        await this.save(channelKey);
        return;
      case 'accept':
        await this.decide(channelKey, 'Accept');
        return;
      case 'reject':
        await this.decide(channelKey, 'Reject');
        return;
      case 'reaffirm':
        await this.decide(channelKey, 'Reaffirm');
        return;
      case 'regenerate':
        await this.send([channelKey]);
        return;
      default:
        await this.copy(channelKey);
    }
  }

  /** The creator's words for one channel, as a new revision. Their words stay in the box if it fails. */
  private async save(channelKey: string): Promise<void> {
    const contextId = this.creativeContextId();
    const body = this.state().unsaved[channelKey];
    const channel = this.channelOf(channelKey);
    if (contextId === null || body === undefined || channel === null) return;

    this.mark(channelKey, { saving: true, problem: '' });

    const outcome = await this.service.edit(this.workspaceSlug(), contextId, channelKey, {
      body,
      expectedLatestRevisionId: channel.latest.id,
    });

    if (this.gone) return;

    this.mark(channelKey, { saving: false });

    if (outcome.status === 'saved') {
      // Cleared only now. Until the server has them, the creator's words are the only copy.
      this.clearUnsaved(channelKey);
      this.packageSignal.set(outcome.package);
      this.announced.emit('Your words were saved.');
      return;
    }

    this.report(channelKey, outcome, 'Your words could not be saved just now. They are still here.');
  }

  private async decide(channelKey: string, decision: ChannelPostDecision): Promise<void> {
    const contextId = this.creativeContextId();
    const channel = this.channelOf(channelKey);
    if (contextId === null || channel === null) return;

    this.mark(channelKey, { deciding: true, problem: '' });

    const outcome = await this.service.decide(this.workspaceSlug(), contextId, channelKey, {
      decision,
      // Named for the two decisions that are about particular words, and null for the one that is not.
      revisionId: decision === 'Reaffirm' ? null : channel.latest.id,
    });

    if (this.gone) return;

    this.mark(channelKey, { deciding: false });

    if (outcome.status === 'saved') {
      this.packageSignal.set(outcome.package);
      this.announced.emit(DECISION_SAID[decision]);
      return;
    }

    this.report(channelKey, outcome, 'That could not be done just now. Nothing was changed.');
  }

  /**
   * Ask for posts on these channels.
   *
   * One path for the first request and for writing one channel again, because they are the same request — and
   * naming one channel is exactly what leaves the others alone.
   */
  private async send(channelKeys: readonly string[]): Promise<void> {
    const contextId = this.creativeContextId();
    if (contextId === null) return;

    this.requestProblem.set('');
    this.copiedChannel.set(null);
    this.writing.set(channelKeys);

    const requestId = await this.tracker.submit(() =>
      this.service.request(this.workspaceSlug(), { creativeContextId: contextId, channelKeys }, crypto.randomUUID()),
    );

    if (this.gone) return;

    if (requestId === null) {
      this.writing.set([]);
      this.requestProblem.set(REQUEST_PROBLEMS[this.tracker.phase().kind] ?? '');
      return;
    }

    this.changed.emit({ ...this.state(), requestId, requestedChannelKeys: channelKeys });
  }

  /** Copy what this channel is showing: the open edit, then what was accepted, then the newest. */
  private async copy(channelKey: string): Promise<void> {
    const channel = this.channelOf(channelKey);
    if (channel === null) return;

    const text = channelPostCopyText(channel, this.state().unsaved[channelKey] ?? null);
    const copied = await this.clipboard.copy(text);

    if (this.gone) return;

    this.copiedChannel.set(copied ? channelKey : null);
    this.announced.emit(
      copied ? 'Copied.' : 'Your browser would not let us copy. Select the words and copy them yourself.',
    );
  }

  private async load(slug: string, contextId: string | null): Promise<void> {
    this.packageSignal.set(null);
    this.busy.set(new Map());
    this.copiedChannel.set(null);
    this.loadFailed.set(false);

    if (contextId === null) {
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    await this.reload(slug, contextId, null);

    if (!this.gone) this.loading.set(false);
  }

  /** Read the package again. A failed read leaves whatever is on screen rather than blanking it. */
  private async reload(slug: string, contextId: string | null, then: (() => void) | null): Promise<void> {
    if (contextId === null) return;

    const outcome = await this.service.readPackage(slug, contextId);
    if (this.gone) return;

    if (outcome.status === 'found') {
      this.packageSignal.set(outcome.package);
      this.loadFailed.set(false);
      then?.();
      return;
    }

    // `not_found` is a piece of work this workspace does not have, which the caller's own screen reports;
    // here it reads the same as a failed read, because either way there is nothing to show.
    this.loadFailed.set(true);
  }

  private report(channelKey: string, outcome: ChannelPostWriteOutcome, fallback: string): void {
    const problem =
      outcome.status === 'stale'
        ? 'This post changed somewhere else since you opened it. Nothing was replaced — read the latest and try again.'
        : outcome.status === 'decision_conflict' || outcome.status === 'source_stale' || outcome.status === 'refused'
          ? outcome.message
          : outcome.status === 'forbidden'
            ? 'You do not have permission to change posts in this workspace.'
            : fallback;

    this.mark(channelKey, { problem });
    this.announced.emit(problem);

    // The server has moved on, so what is on screen is behind it. Read it again rather than leave a card
    // offering a decision about words that are no longer the newest.
    if (outcome.status === 'stale' || outcome.status === 'decision_conflict') {
      void this.reload(this.workspaceSlug(), this.creativeContextId(), null);
    }
  }

  private channelOf(channelKey: string): ChannelPostChannel | null {
    return this.packageSignal()?.channels.find((channel) => channel.channelKey === channelKey) ?? null;
  }

  private showingBodyOf(channelKey: string): string {
    const channel = this.channelOf(channelKey);
    return channel === null ? '' : (channel.accepted ?? channel.latest).body;
  }

  private channelNames(): ReadonlyMap<string, string> {
    return new Map(this.channels().map((channel) => [channel.key, channel.displayName]));
  }

  private withoutWritten(keys: readonly string[]): readonly string[] {
    const offered = new Set(this.options().map((option) => option.value));

    return keys.filter((key) => offered.has(key));
  }

  private setUnsaved(channelKey: string, body: string): void {
    this.changed.emit({ ...this.state(), unsaved: { ...this.state().unsaved, [channelKey]: body } });
  }

  private clearUnsaved(channelKey: string): void {
    const { [channelKey]: _removed, ...rest } = this.state().unsaved;
    this.changed.emit({ ...this.state(), unsaved: rest });
  }

  private mark(channelKey: string, change: Partial<ChannelPostBusy>): void {
    this.busy.update((current) => {
      const next = new Map(current);
      next.set(channelKey, { ...(current.get(channelKey) ?? IDLE_CHANNEL_POST_BUSY), ...change });

      return next;
    });
  }
}

/** What each decision is announced as. One sentence each, in the creator's terms. */
const DECISION_SAID: Readonly<Record<ChannelPostDecision, string>> = {
  Accept: 'Accepted.',
  Reject: 'Turned down. What you accepted before still stands.',
  Regenerate: 'Writing it again…',
  Reaffirm: 'Kept as it is, against the recipe as it stands now.',
};

/** What a refused request says, by the phase the tracker reached. */
const REQUEST_PROBLEMS: Readonly<Record<string, string>> = {
  not_enabled: 'Writing posts is not switched on for this workspace yet.',
  forbidden: 'You do not have permission to write posts in this workspace.',
  key_reused: 'That did not go through. Asking again is safe.',
  refused: 'That piece of work could not be written for. It may have been removed.',
  gone: 'That request is no longer there. Asking again is the way on.',
  unavailable: 'Your posts could not be asked for just now. Trying again is safe — it will not write two sets.',
};
