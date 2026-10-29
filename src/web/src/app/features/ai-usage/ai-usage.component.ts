import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal } from '@angular/core';
import {
  CpButtonComponent,
  CpCardComponent,
  CpCheckboxComponent,
  CpCardGridComponent,
  CpFactComponent,
  CpFactsComponent,
  CpListShellComponent,
  CpListShellState,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpProgressComponent,
  CpProgressTone,
  CpSkeletonComponent,
  CpStatTileComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { RouterLink } from '@angular/router';

import { injectRouteTitle } from '../../core/route-title';

import { AiTaskType } from '../../models/ai-proposal.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { allowanceFigure, allowanceIsCountable, formatAllowanceAmount, formatAllowanceReset } from '../../shared/allowance-text';

/**
 * What each capability is called, in the creator's language.
 *
 * A `Record` over the whole union rather than a lookup with a fallback: adding a capability should fail the
 * build here and make somebody choose a name, not quietly print `IngredientSubstitution` on a page that has
 * promised not to.
 */
const TASK_LABELS: Record<AiTaskType, string> = {
  Unspecified: 'Other AI work',
  Diagnostic: 'Service checks',
  RecipeConcepts: 'Recipe ideas',
  RecipeFirstDraft: 'First drafts',
  RecipeRevision: 'Revisions',
  IngredientSubstitution: 'Ingredient swaps',
  RecipeAdaptation: 'Adaptations',
  RecipeReview: 'Recipe reviews',
  ProposalExplanation: 'Explanations',
  EditorialPackage: 'Editorial packages',
  SeoPackage: 'SEO packages',
  BrandGuideProposal: 'Brand guide proposals',
  PhotographyConcept: 'Photography concepts',
  ImagePrompt: 'Image prompts',
  BrandStyleTestDrive: 'Style test drives',
  ReferenceImageAnalysis: 'Reference image readings',
  DishFacetSuggestion: 'Dish name readings',
  ChannelPosts: 'Channel posts',
  LongFormOutline: 'Article and newsletter plans',
  LongFormSection: 'Article and newsletter sections',
  ImageGeneration: 'Pictures',
};

/** A workspace the account has left keeps its spend and loses its name (USAGE-008). */
const WITHHELD_WORKSPACE = 'A workspace you’ve left';

/** What a pack tile says beside its figure: when packs are spent, and that they keep (PRICE-008, B-50). */
const PACK_CONTEXT = 'Used after your allowance. They don’t expire.';

/** Sentences joined with one space, leaving out any that are empty, so nothing trails or doubles a space. */
function sentences(...parts: readonly string[]): string {
  return parts.filter((part) => part.length > 0).join(' ');
}

const COULD_NOT_LOAD = 'We couldn’t load your AI allowance just now. Check your connection and try again.';

/**
 * The signed-in account's AI allowance: what is left, when it comes back, and what it has gone on
 * (USAGE-008/010).
 *
 * <strong>Account-scoped, deliberately not under a workspace.</strong> The answer spans every workspace the
 * creator works in, so a workspace-scoped route could only narrow it to one or answer about workspaces it
 * does not name.
 *
 * <strong>Credits and dates, never plumbing.</strong> No provider, deployment or model name appears here, and
 * the headline figure is what a creator has left rather than a token count — the API returns none of those,
 * and EASE-004 asks that none of them reach the screen.
 */
@Component({
  selector: 'cp-ai-usage',
  standalone: true,
  imports: [
    RouterLink,
    CpPageHeadComponent,
    CpButtonComponent,
    CpCardComponent,
    CpCardGridComponent,
    CpCheckboxComponent,
    CpFactComponent,
    CpFactsComponent,
    CpListShellComponent,
    CpNoticeComponent,
    CpProgressComponent,
    CpSkeletonComponent,
    CpStatTileComponent,
    CpStatusPillComponent,
    CpTopicCardComponent,
  ],
  templateUrl: './ai-usage.component.html',
  styleUrl: './ai-usage.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AiUsageComponent {
  /** The page's name, from its own route: what `cp-page-head` prints as the one `h1`. */
  protected readonly pageTitle = injectRouteTitle();
  private readonly usage = inject(AiUsageService);

  protected readonly state = this.usage.state;
  protected readonly allowance = this.usage.allowance;
  protected readonly retrying = signal(false);

  /** How many times the creator has asked again, so a repeated failure still announces. */
  private readonly attempts = signal(0);

  constructor() {
    // refresh, not ensureLoaded: the shell has usually loaded this already for its topbar indicator, and
    // this page is the authoritative view of the figure. Arriving here should not show a balance from the
    // start of the session.
    void this.usage.refresh();
  }

  /**
   * Whether the creator wants to be told when their credits run low (B-59).
   *
   * A linked signal rather than a plain read of the service: the control moves the moment it is clicked, and
   * re-follows the server whenever the allowance is read again — including after a save, which is what puts
   * the control back if the save did not take.
   */
  protected readonly noticeEnabled = linkedSignal(() => this.usage.lowCreditNoticeEnabled());

  /** True while the choice is on its way, so a double click cannot send two. */
  protected readonly savingNotice = signal(false);

  /** Set when the choice could not be recorded. Cleared by the next attempt. */
  protected readonly noticeProblem = signal(false);

  /**
   * Records the choice, having already shown it.
   *
   * A failure puts the control back rather than leaving a creator believing they have switched something off
   * that is still on — the one outcome worth a message on a page whose other states are all figures.
   */
  protected async setNoticeEnabled(enabled: boolean): Promise<void> {
    // A second press while the first is in flight is ignored rather than disabled away, so the control the
    // creator is standing on keeps focus.
    if (this.savingNotice()) return;

    this.noticeEnabled.set(enabled);
    this.noticeProblem.set(false);
    this.savingNotice.set(true);

    try {
      if (!(await this.usage.setLowCreditNoticeEnabled(enabled))) {
        this.noticeEnabled.set(!enabled);
        this.noticeProblem.set(true);
      }
    } finally {
      this.savingNotice.set(false);
    }
  }

  /** The figures on screen, or null while there are none. */
  protected readonly figures = computed(() => {
    const allowance = this.allowance();
    return allowance.kind === 'unknown' ? null : allowance.period;
  });

  protected readonly headline = computed(() => {
    const figures = this.figures();
    if (figures === null) return '';

    const left = formatAllowanceAmount(Math.max(0, figures.remaining), figures.unit);

    // A unit this product does not quote figures in is described rather than counted, so a token-denominated
    // period cannot put a raw token count on screen wearing a different noun (EASE-004).
    return left === null ? 'Allowance remaining' : `${left} left`;
  });

  /**
   * "this month" rather than "this period", which is what `periodLength` is published for.
   */
  protected readonly periodWord = computed(() => {
    switch (this.figures()?.periodLength) {
      case 'Daily':
        return 'today';
      case 'Weekly':
        return 'this week';
      case 'Monthly':
        return 'this month';
      default:
        return 'this period';
    }
  });

  /**
   * What rolled in from last period, said rather than folded into the total.
   *
   * The field exists so a roll is visible: without it "640 left of 1,000" hides that 400 of the 1,000 was
   * last period's unspent balance rather than this period's grant.
   */
  protected readonly carriedOverLine = computed(() => {
    const figures = this.figures();
    if (figures === null || figures.carriedOver <= 0) return '';

    const carried = formatAllowanceAmount(figures.carriedOver, figures.unit);

    return carried === null ? '' : `Includes ${carried} carried over from last time.`;
  });

  /**
   * The allowance as figures read at a glance (AU.6.4, APP-008): what is left, what has been used, and the
   * whole allowance — each the server's own number, formatted and nothing more. "Left" is the server's
   * `remaining`, which also holds back what requests still running have set aside, so the three need not add up
   * while one is in flight; the server's own `reserved` says how much, beside "left", rather than the page working
   * it out. None at all for a unit this product describes rather than counts: the card's headline says it in
   * words instead.
   */
  protected readonly tiles = computed(() => {
    const figures = this.figures();
    if (figures === null) return null;

    const left = allowanceFigure(Math.max(0, figures.remaining), figures.unit);
    const used = allowanceFigure(figures.consumed, figures.unit);
    const whole = allowanceFigure(figures.allowance, figures.unit);
    if (left === null || used === null || whole === null) return null;

    const tiles = [
      { id: 'left', value: left.value, label: `${left.noun} left`, context: this.leftContext() },
      { id: 'used', value: used.value, label: `${used.noun} used ${this.periodWord()}`, context: this.usedContext() },
      { id: 'allowance', value: whole.value, label: `${whole.noun} in your allowance ${this.periodWord()}`, context: this.carriedOverLine() },
    ];

    // The packs as a figure of their own, beside the allowance and never added to it (AP.5.3). None until there
    // are some: packs cannot be bought yet, and "0 in credit packs" would read as something missing.
    const packs = figures.packRemaining > 0 ? allowanceFigure(figures.packRemaining, figures.unit) : null;
    if (packs !== null) {
      tiles.push({ id: 'packs', value: packs.value, label: `${packs.noun} in credit packs`, context: PACK_CONTEXT });
    }

    return tiles;
  });

  /**
   * The allowance is used up and the packs are what AI help is coming from now (AP.5.2). Not `exhausted` — the
   * service keeps that for when both are gone — so this is the one healthy state the page has to name itself.
   */
  protected readonly onPacks = computed(() => {
    const allowance = this.allowance();
    return allowance.kind === 'healthy' && allowance.period.remaining <= 0 && allowance.period.packRemaining > 0;
  });

  /** The plan, when this host names one. */
  protected readonly plan = computed(() => this.currentUsage()?.plan ?? null);

  /**
   * Whether the allowance comes back at the reset — the server's answer, false for a trial's credits (B-48). With
   * no plan named, it renews as allowances always have.
   */
  private readonly renews = computed(() => this.plan()?.allowanceRenews ?? true);

  /** The reset as a fact beside the plan: the date, or that a trial's credits do not come back. */
  protected readonly resetFact = computed(() => {
    const figures = this.figures();
    if (figures === null) return null;

    if (!this.renews()) return { label: 'Trial credits', value: 'Not renewed' };

    const when = formatAllowanceReset(figures.resetsAt, figures.timeZoneId);
    return when === null ? null : { label: 'Allowance comes back', value: when };
  });

  /**
   * What "left" needs said beside it, in words: that these are the last figures we had, when they could not be
   * checked, and what requests still running are holding back, when the server says any are.
   */
  private readonly leftContext = computed(() => {
    const parts: string[] = [];
    if (this.state().status === 'degraded') parts.push('The last figures we had.');

    const figures = this.figures();
    const reserved = this.currentUsage()?.period.reserved ?? 0;
    const held = figures !== null && reserved > 0 ? formatAllowanceAmount(reserved, figures.unit) : null;
    if (held !== null) parts.push(`${held} set aside for requests still running.`);

    return parts.join(' ');
  });

  /**
   * What the used credits went on — pictures, and everything else — each the server's own total (AP.2.6). Nothing
   * said before anything has been spent.
   */
  private readonly usedContext = computed(() => {
    const usage = this.currentUsage();
    const figures = this.figures();
    if (usage === null || figures === null || (usage.pictures === 0 && usage.writing === 0)) return '';

    const pictures = formatAllowanceAmount(usage.pictures, figures.unit);
    const writing = formatAllowanceAmount(usage.writing, figures.unit);

    return pictures === null || writing === null ? '' : `${pictures} on pictures and ${writing} on writing.`;
  });

  /** Skeleton places for the figures while they load. */
  protected readonly tilePlaces = [0, 1, 2] as const;

  protected readonly resetLine = computed(() => {
    const figures = this.figures();
    if (figures === null) return '';

    // A trial's credits are granted once: promising them back on a date would be a promise the server will not keep.
    if (!this.renews()) return 'Trial credits are granted once, so they don’t come back.';

    const when = formatAllowanceReset(figures.resetsAt, figures.timeZoneId);

    return when === null ? 'Your allowance comes back when the period resets.' : `Comes back on ${when}.`;
  });

  /** What the meter prints and announces — the amount, never a bare percentage. */
  protected readonly meterText = computed(() => {
    const figures = this.figures();
    if (figures === null) return '';

    // "640 AI credits left of 1,000" — the noun is said once. A unit this product does not quote figures in
    // gets the headline alone, which already describes rather than counts.
    return allowanceIsCountable(figures.unit)
      ? `${this.headline()} of ${(Math.round(figures.allowance * 100) / 100).toLocaleString()}`
      : this.headline();
  });

  protected readonly meterTone = computed<CpProgressTone>(() => {
    // Nothing is wrong: the packs are doing what they were bought for. The untoned fill, so the meter does not
    // warn about a state the pill and the words call ordinary.
    if (this.onPacks()) return 'default';

    switch (this.allowance().kind) {
      case 'nearly-spent':
        return 'warning';
      case 'exhausted':
      case 'suspended':
        return 'error';
      default:
        return 'success';
    }
  });

  /** The state in words and a glyph. The meter's colour repeats this; it never carries it alone. */
  protected readonly statusLabel = computed(() => {
    if (this.onPacks()) return 'Using credit packs';

    switch (this.allowance().kind) {
      case 'nearly-spent':
        return 'Nearly spent';
      case 'exhausted':
        return 'Spent';
      case 'suspended':
        return 'Switched off';
      case 'degraded':
        return 'Not up to date';
      default:
        return 'Plenty left';
    }
  });

  protected readonly statusTone = computed<CpStatusPillTone>(() => {
    if (this.onPacks()) return 'progress';

    switch (this.allowance().kind) {
      case 'nearly-spent':
        return 'warning';
      case 'exhausted':
      case 'suspended':
        return 'error';
      case 'degraded':
        return 'stale';
      default:
        return 'success';
    }
  });

  /**
   * What the surface says beneath the meter. Suspension says something different from exhaustion, because an
   * allowance comes back on its own and a suspension does not.
   */
  protected readonly explanation = computed(() => {
    // Said once in the card: when the facts beside the plan carry the reset, the sentence leaves it to them.
    const reset = this.resetInFacts() ? '' : this.resetLine();

    if (this.onPacks()) {
      return sentences(`Your allowance is used up ${this.periodWord()}, so AI help now comes from ${this.packsPhrase()}.`, reset);
    }

    switch (this.allowance().kind) {
      case 'suspended':
        return 'AI assistance is switched off for your account. Get in touch and we can look into it. Everything else in CreatorPantry works as usual.';
      case 'exhausted':
        return sentences('You can keep writing and editing recipes as usual.', reset);
      case 'nearly-spent':
        // The server's line is about the allowance alone, as a picture quote's is (AP.2.6); the packs are why
        // "not much more" would be untrue, so they are said rather than folded into the warning.
        return this.figures()!.packRemaining > 0
          ? sentences(`Your allowance is nearly spent ${this.periodWord()}. After that, AI help comes from ${this.packsPhrase()}.`, reset)
          : sentences(`You can still ask for AI help, but not much more ${this.periodWord()}.`, reset);
      case 'degraded':
        return 'We couldn’t check just now, so these are the last figures we had.';
      default:
        return reset;
    }
  });

  /** Whether the plan's facts say when the allowance comes back, so the explanation need not say it again. */
  private readonly resetInFacts = computed(() => this.plan() !== null && this.resetFact() !== null);

  protected readonly byTask = computed(() => {
    const usage = this.currentUsage();
    if (usage === null) return [];

    return [...usage.byTask]
      .sort((left, right) => right.amount - left.amount)
      .map((row) => ({ ...row, label: TASK_LABELS[row.taskType], amountText: this.rowAmount(row.amount) }));
  });

  protected readonly byWorkspace = computed(() => {
    const usage = this.currentUsage();
    if (usage === null) return [];

    return [...usage.byWorkspace]
      .sort((left, right) => right.amount - left.amount)
      .map((row) => ({
        ...row,
        label: row.workspaceName ?? WITHHELD_WORKSPACE,
        amountText: this.rowAmount(row.amount),
      }));
  });

  protected readonly summaryState = computed<'loading' | 'error' | 'ready'>(() => {
    const status = this.state().status;
    if (status === 'loading') return 'loading';
    if (status === 'error') return 'error';

    return 'ready';
  });

  protected readonly errorMessage = COULD_NOT_LOAD;

  protected taskListState(): CpListShellState {
    return this.breakdownState(this.byTask().length);
  }

  protected workspaceListState(): CpListShellState {
    return this.breakdownState(this.byWorkspace().length);
  }

  /**
   * One sentence for the live region, so a screen reader hears the whole state once rather than three
   * fragments arriving in whatever order change detection produced them.
   */
  protected readonly announcement = computed(() => {
    switch (this.summaryState()) {
      case 'loading':
        return 'Loading your AI allowance.';

      // A second failure must not produce the identical string, or the live region's content is unchanged
      // and nothing is announced — leaving the only feedback a button label flicking back and forth.
      case 'error':
        return this.attempts() === 0 ? COULD_NOT_LOAD : `Still couldn’t load your AI allowance. ${COULD_NOT_LOAD}`;

      // The reset is the facts' alone on screen when a plan is named, and still part of the one sentence heard.
      default:
        return sentences(
          `${this.statusLabel()}. ${this.headline()}.`,
          this.explanation(),
          this.resetInFacts() ? this.resetLine() : '',
        );
    }
  });

  protected async retry(): Promise<void> {
    // The button stays enabled so focus is not dropped mid-request, which means it can be pressed again —
    // so the guard lives here rather than in the DOM.
    if (this.retrying()) return;

    this.retrying.set(true);
    try {
      await this.usage.refresh();
    } finally {
      this.retrying.set(false);
      this.attempts.update((count) => count + 1);
    }
  }

  /** "your credit packs, which hold 400 AI credits" — without the count for a unit this page does not quote. */
  private packsPhrase(): string {
    const figures = this.figures();
    const held = figures === null ? null : formatAllowanceAmount(figures.packRemaining, figures.unit);

    return held === null ? 'your credit packs' : `your credit packs, which hold ${held}`;
  }

  /** A breakdown row's amount, in the period's own unit. Shortened: the header already says what these are. */
  private rowAmount(amount: number): string {
    const figures = this.figures();
    const text = figures === null ? null : formatAllowanceAmount(amount, figures.unit);

    return text === null ? '' : text.replace('AI ', '');
  }

  private currentUsage() {
    const state = this.state();
    return state.status === 'ready' || state.status === 'degraded' ? state.usage : null;
  }

  private breakdownState(count: number): CpListShellState {
    const summary = this.summaryState();
    if (summary !== 'ready') return summary;

    return count === 0 ? 'empty' : 'ready';
  }
}
