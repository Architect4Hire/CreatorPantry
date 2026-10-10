import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpFieldComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import {
  ChannelPostChannel,
  ChannelPostRevision,
  channelPostCount,
  channelPostCountText,
  channelPostShowing,
  isGeneratedAwaitingReview,
} from '../../models/channel-post.models';

/** What one channel is doing right now, beyond what the server last said about it. */
export interface ChannelPostBusy {
  /** The creator's unsaved words for this channel, or null when they are not editing it. */
  readonly editing: string | null;
  readonly saving: boolean;
  readonly deciding: boolean;
  /** True while a fresh post for *this* channel is being written. Never set by a neighbour's run. */
  readonly regenerating: boolean;
  /** What the last write said, in the creator's terms. Empty when there is nothing to report. */
  readonly problem: string;
}

export const IDLE_CHANNEL_POST_BUSY: ChannelPostBusy = {
  editing: null,
  saving: false,
  deciding: false,
  regenerating: false,
  problem: '',
};

/** What a creator asked for on one channel. The caller writes it; this card decides nothing. */
export type ChannelPostIntent =
  | { readonly kind: 'edit' }
  | { readonly kind: 'typed'; readonly body: string }
  | { readonly kind: 'save' }
  | { readonly kind: 'cancel' }
  | { readonly kind: 'accept' }
  | { readonly kind: 'reject' }
  | { readonly kind: 'reaffirm' }
  | { readonly kind: 'regenerate' }
  | { readonly kind: 'copy' };

/** How each status reads, as a word and as a pill tone with a glyph of its own. */
const STATUS_WORDS: Readonly<Record<string, string>> = {
  Proposed: 'Waiting for you',
  Accepted: 'Accepted',
  Rejected: 'Not used',
  NeedsReview: 'Needs another look',
};

const STATUS_TONES: Readonly<Record<string, CpStatusPillTone>> = {
  Proposed: 'progress',
  Accepted: 'success',
  Rejected: 'neutral',
  NeedsReview: 'stale',
};

/**
 * One channel's post, reviewed on its own (AF.6.5).
 *
 * **Written as a shared composition because two screens review posts.** The Content Pipeline's posts step is
 * the first; master 13.3's Social Studio is the second, and a second copy of the limit reading, the generated
 * marker and the four decisions would be a second thing to keep honest.
 *
 * **Controlled.** It renders the channel it is handed plus whatever its caller says is in flight, and emits
 * what the creator asked for. Nothing here writes, so what is on screen is always what the caller holds — and
 * an unsaved edit survives a failed save, a neighbour's regeneration and leaving the step, because the words
 * live in the caller's kept state rather than in this component.
 *
 * **Nothing appears accepted on its own.** A generated body arrives awaiting a decision and says so, in a
 * badge as well as a pill; the badge goes when the creator's own words replace it, because they are then not
 * generated content any more.
 *
 * **The limit is never a colour alone.** The pill carries a glyph per tone and the figure is written out —
 * "312 of 280 — 32 over" — so the same fact reaches a creator who cannot tell the two pills apart. An
 * over-limit body is shown as written and never trimmed.
 */
@Component({
  selector: 'cp-channel-post-review',
  standalone: true,
  imports: [CpBadgeComponent, CpButtonComponent, CpFieldComponent, CpNoticeComponent, CpStatusPillComponent],
  templateUrl: './channel-post-review.component.html',
  styleUrl: './channel-post-review.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChannelPostReviewComponent {
  readonly channel = input.required<ChannelPostChannel>();
  /** What a creator calls this channel. The key is the fallback, never the label when a name is known. */
  readonly channelName = input.required<string>();
  readonly busy = input<ChannelPostBusy>(IDLE_CHANNEL_POST_BUSY);
  /** False where the creator may read but not write — a Viewer, or a surface that only shows. */
  readonly canWrite = input(true);
  /** False while the account's allowance will not cover another request, so only regenerating is held. */
  readonly canRegenerate = input(true);
  /** True once a copy of this channel's words landed on the clipboard, so the button can say so. */
  readonly copied = input(false);
  /** Prefixes this card's ids, so several cards on one screen name their own regions. */
  readonly idPrefix = input('cp-channel-post');

  readonly intent = output<ChannelPostIntent>();

  /** The words on screen: the accepted ones where a decision put them there, else the newest. */
  protected readonly showing = computed<ChannelPostRevision>(() => channelPostShowing(this.channel()));

  /** True while the creator has this channel open in a box. */
  protected readonly isEditing = computed(() => this.busy().editing !== null);

  protected readonly statusWord = computed(() => STATUS_WORDS[this.channel().status] ?? 'Waiting for you');

  protected readonly statusTone = computed<CpStatusPillTone>(
    () => STATUS_TONES[this.channel().status] ?? 'neutral',
  );

  /** True for words a model wrote that the creator has neither replaced nor decided about. */
  protected readonly isGenerated = computed(() => isGeneratedAwaitingReview(this.channel()));

  /**
   * The limit as it reads right now.
   *
   * While editing it is the browser's live count against the limit the last saved revision was measured
   * with, and it says so — the server counts the way the channel counts, so the two can differ and the
   * server's figure replaces this one on save. Otherwise it is the server's own measurement.
   */
  protected readonly limit = computed(() => {
    const showing = this.showing();
    const editing = this.busy().editing;

    if (editing !== null) {
      const count = channelPostCount(editing);
      const over = showing.characterLimit !== null && count > showing.characterLimit;

      return {
        text: channelPostCountText(count, showing.characterLimit),
        tone: (over ? 'warning' : 'neutral') satisfies CpStatusPillTone as CpStatusPillTone,
        word: over ? 'Over the limit as you type' : 'As you type',
        measured: false,
      };
    }

    if (showing.limitStatus === 'NotChecked' || showing.characterCount === null) {
      return { text: '', tone: 'neutral' as CpStatusPillTone, word: '', measured: false };
    }

    const over = showing.limitStatus === 'Over';

    return {
      text: channelPostCountText(showing.characterCount, showing.characterLimit),
      tone: (over ? 'warning' : 'success') satisfies CpStatusPillTone as CpStatusPillTone,
      word: over ? 'Longer than this channel allows' : 'Fits this channel',
      measured: true,
    };
  });

  /** The words in the box, or the ones on screen when there is no box. */
  protected readonly bodyText = computed(() => this.busy().editing ?? this.showing().body);

  /** True while something is in flight for this channel, so its own controls are held. */
  protected readonly working = computed(() => {
    const busy = this.busy();
    return busy.saving || busy.deciding || busy.regenerating;
  });

  /** Accepting is offered only where there is something awaiting a decision. */
  protected readonly canAccept = computed(() => this.channel().status === 'Proposed');

  /** Reaffirming is the one way back for accepted words whose recipe has moved on. */
  protected readonly canReaffirm = computed(() => this.channel().status === 'NeedsReview');

  /** Said under the pill when accepted words are no longer about the recipe as it stands. */
  protected readonly isStale = computed(() => {
    const channel = this.channel();
    return channel.accepted !== null && channel.isCurrent === false;
  });

  protected emit(intent: ChannelPostIntent): void {
    this.intent.emit(intent);
  }

  protected onTyped(event: Event): void {
    const target = event.target as HTMLTextAreaElement | null;
    this.emit({ kind: 'typed', body: target?.value ?? '' });
  }
}
