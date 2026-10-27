import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  CpButtonComponent,
  CpFieldComponent,
  CpGeneratedComponent,
  CpNoticeComponent,
  CpProgressComponent,
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
  | { readonly kind: 'copy' }
  | { readonly kind: 'download' };

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

/** How a post's length reads: the figure, how far along the channel's limit it is, and the pill that says so. */
interface ChannelPostLimitReading {
  /** "46 of 2200 characters", "312 of 280 — 32 over", or empty when nobody measured it. */
  readonly text: string;
  /** True when the channel has a limit to measure against, which is when a bar is drawn. */
  readonly limited: boolean;
  /** How far along the limit, held to 0–100: the figure, not the bar, says by how much a post is over. */
  readonly percent: number;
  readonly over: boolean;
  readonly tone: CpStatusPillTone;
  readonly word: string;
  readonly measured: boolean;
}

const NOT_MEASURED: ChannelPostLimitReading = {
  text: '',
  limited: false,
  percent: 0,
  over: false,
  tone: 'neutral',
  word: '',
  measured: false,
};

/**
 * The figure and the bar for a count against a limit the channel's profile set, or against none.
 *
 * `over` is the verdict the pill states — the server's for a saved revision, the live count's while typing — so
 * the figure, the bar and the pill can never say different things. A server that calls a post over with a figure
 * that does not show it (it counts the way the channel counts) gets a full bar and no "— N over" it cannot back.
 */
function lengthOf(
  count: number,
  limit: number | null,
  over: boolean,
): Pick<ChannelPostLimitReading, 'text' | 'limited' | 'percent' | 'over'> {
  if (limit === null || limit <= 0)
    return { text: channelPostCountText(count, null), limited: false, percent: 0, over };

  const by = count - limit;

  return {
    text: over && by > 0 ? `${count} of ${limit} — ${by} over` : `${count} of ${limit} characters`,
    limited: true,
    percent: over ? 100 : Math.min(100, Math.round((count / limit) * 100)),
    over,
  };
}

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
 * **Nothing appears accepted on its own.** A generated body arrives awaiting a decision and says so: it sits on
 * `cp-generated`, named for the channel it was written for, with the decisions about it last inside it, and a
 * pill says it is waiting. The surface goes when the creator's own words replace the model's — or while they are
 * typing their own — because they are then not generated content any more (APP-006).
 *
 * **The limit is never a colour alone.** The length is a `cp-progress` bar against the limit the channel's profile
 * set, which the server measured (content.md: channel constraints live in channel profiles, not page text); a pill
 * with a glyph per tone says whether it fits, and the figure is written out — "312 of 280 — 32 over" — so the same
 * fact reaches a creator who cannot tell two colours apart. The bar stops at full, and an over-limit body is shown
 * as written and never trimmed.
 */
@Component({
  selector: 'cp-channel-post-review',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    CpButtonComponent,
    CpFieldComponent,
    CpGeneratedComponent,
    CpNoticeComponent,
    CpProgressComponent,
    CpStatusPillComponent,
  ],
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

  protected readonly statusTone = computed<CpStatusPillTone>(() => STATUS_TONES[this.channel().status] ?? 'neutral');

  /** True for words a model wrote that the creator has neither replaced nor decided about. */
  protected readonly isGenerated = computed(() => isGeneratedAwaitingReview(this.channel()));

  /**
   * The limit as it reads right now.
   *
   * While editing it is the browser's live count against the limit the last saved revision was measured
   * with, and it says so — the server counts the way the channel counts, so the two can differ and the
   * server's figure replaces this one on save. Otherwise it is the server's own measurement.
   */
  protected readonly limit = computed<ChannelPostLimitReading>(() => {
    const showing = this.showing();
    const editing = this.busy().editing;

    if (editing !== null) {
      const count = channelPostCount(editing);
      const over = showing.characterLimit !== null && count > showing.characterLimit;

      return {
        ...lengthOf(count, showing.characterLimit, over),
        tone: over ? 'warning' : 'neutral',
        word: over ? 'Over the limit as you type' : 'As you type',
        measured: false,
      };
    }

    if (showing.limitStatus === 'NotChecked' || showing.characterCount === null) return NOT_MEASURED;

    const over = showing.limitStatus === 'Over';

    return {
      // The server's verdict, not this page's arithmetic: it counts the way the channel counts.
      ...lengthOf(showing.characterCount, showing.characterLimit, over),
      tone: over ? 'warning' : 'success',
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
