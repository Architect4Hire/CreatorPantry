import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import {
  CpButtonComponent,
  CpCardComponent,
  CpListShellComponent,
  CpListShellState,
  CpProgressComponent,
  CpProgressTone,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { AiTaskType } from '../../models/ai-proposal.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { allowanceIsCountable, formatAllowanceAmount, formatAllowanceReset } from '../../shared/allowance-text';

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
};

/** A workspace the account has left keeps its spend and loses its name (USAGE-008). */
const WITHHELD_WORKSPACE = 'A workspace you’ve left';

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
    CpButtonComponent,
    CpCardComponent,
    CpListShellComponent,
    CpProgressComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './ai-usage.component.html',
  styleUrl: './ai-usage.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AiUsageComponent {
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

  protected readonly resetLine = computed(() => {
    const figures = this.figures();
    if (figures === null) return '';

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
    switch (this.allowance().kind) {
      case 'suspended':
        return 'AI assistance is switched off for your account. Get in touch and we can look into it. Everything else in CreatorPantry works as usual.';
      case 'exhausted':
        return `You can keep writing and editing recipes as usual. ${this.resetLine()}`;
      case 'nearly-spent':
        return `You can still ask for AI help, but not much more ${this.periodWord()}. ${this.resetLine()}`;
      case 'degraded':
        return 'We couldn’t check just now, so these are the last figures we had.';
      default:
        return this.resetLine();
    }
  });

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

      default:
        return `${this.statusLabel()}. ${this.headline()}. ${this.explanation()}`;
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
