import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { concat, of, switchMap } from 'rxjs';
import {
  CpButtonComponent,
  CpCardComponent,
  CpCardGridComponent,
  CpCheckboxComponent,
  CpChipComponent,
  CpChipGroupComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpOptionCardComponent,
  CpOptionFact,
  CpPageHeadComponent,
  CpSkeletonComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { injectRouteTitle } from '../../core/route-title';
import { BillingAccount, BillingCycle } from '../../models/billing.models';
import { CreditPack, Plan } from '../../models/plan-catalogue.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { BillingCommandOutcome, BillingService } from '../../services/billing.service';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { formatAllowanceReset } from '../../shared/allowance-text';

/** How the page has got on reading the account's billing. */
type LoadState = { readonly kind: 'loading' } | { readonly kind: 'error' } | { readonly kind: 'ready'; readonly account: BillingAccount };

/** A sentence for the page's one fixed place under the head. */
interface PageNews {
  readonly tone: 'neutral' | 'success' | 'warning' | 'error';
  readonly text: string;
}

/** How often, and for how long, the page asks whether what a Checkout bought has landed. */
export const CHECKOUT_POLL_MS = 2000;
export const CHECKOUT_POLL_TRIES = 30;

/** Said under every price, because the provider adds the tax (B-58; owner, 2026-10-29). */
export const TAX_LINE = 'Prices before tax. Any sales tax, VAT or GST is added at checkout.';

const COULD_NOT_LOAD = 'We couldn’t load your billing just now. Check your connection and try again.';
const UNAVAILABLE = 'Payments aren’t available right now. Nothing was charged.';
const NOT_ON_OFFER = 'Payments aren’t available right now.';

/** The places on the page a link or an action lands focus on: each wraps a topic card and takes focus itself. */
export const PLAN_PLACE = 'billing-plan';
export const PLANS_PLACE = 'billing-plans';
export const PACKS_PLACE = 'billing-packs';

/**
 * The account's billing (AP.6.5): the plan it is on, the plans and packs it can buy, the provider's portal, and the
 * low-balance notice's setting. A detail page about the account, beside "AI allowance".
 *
 * **Nothing here buys anything.** Each button asks the server for the provider's page and goes there; what was
 * bought arrives by the provider's webhook, so coming back from Checkout shows a pending state until the server says
 * the plan is in force or the credits are granted (B-57, B-60).
 *
 * **Every figure and every rule is the server's.** Prices, credits and caps are the published catalogue's; which plan
 * is in force, whether the trial has ended, what may be bought and which changes are upgrades are the billing read's.
 * The page formats them and chooses only the words (B-55, B-62).
 */
@Component({
  selector: 'cp-billing',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpCardGridComponent,
    CpCheckboxComponent,
    CpChipComponent,
    CpChipGroupComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpOptionCardComponent,
    CpPageHeadComponent,
    CpSkeletonComponent,
    CpStatusPillComponent,
    CpTopicCardComponent,
  ],
  templateUrl: './billing.component.html',
  styleUrl: './billing.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BillingComponent {
  protected readonly pageTitle = injectRouteTitle();
  private readonly billing = inject(BillingService);
  private readonly catalogueService = inject(PlanCatalogueService);
  private readonly usage = inject(AiUsageService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);

  protected readonly taxLine = TAX_LINE;
  protected readonly couldNotLoad = COULD_NOT_LOAD;
  protected readonly planPlace = PLAN_PLACE;
  protected readonly plansPlace = PLANS_PLACE;
  protected readonly packsPlace = PACKS_PLACE;

  protected readonly state = signal<LoadState>({ kind: 'loading' });
  protected readonly retrying = signal(false);

  /**
   * News for the always-present polite region (DESIGN-SYSTEM.md, "What the product says"): the region is in the DOM
   * before it has text, so what arrives in it is announced. A refusal is the other kind — an alert, newly inserted.
   */
  protected readonly news = signal<PageNews | null>(null);
  protected readonly refusal = signal<string | null>(null);

  /** Bumped to read the catalogue again after it could not be read. */
  private readonly catalogueAsk = signal(0);

  /** The published catalogue: undefined while it is read, null when it could not be, else what is on offer. */
  protected readonly catalogue = toSignal(
    toObservable(this.catalogueAsk).pipe(switchMap(() => concat(of(undefined), this.catalogueService.read()))),
    { initialValue: undefined },
  );

  /**
   * Which cycle the plan cards price: the one the account pays on until the creator picks the other, and their pick
   * after that, through every re-read of the account.
   */
  protected readonly cycle = linkedSignal<BillingCycle | undefined, BillingCycle>({
    source: () => this.account()?.subscription?.cycle ?? undefined,
    computation: (paid, previous) => previous?.value ?? paid ?? 'Monthly',
  });

  /** A command in flight, by what it is for, so its button says so and a second press is ignored. */
  protected readonly busy = signal<string | null>(null);

  protected readonly noticeEnabled = linkedSignal(() => this.usage.lowCreditNoticeEnabled());
  protected readonly savingNotice = signal(false);

  /** One key per logical command, kept across a retry of it and dropped once it has a definite answer. */
  private readonly keys = new Map<string, string>();
  private polling = false;
  private stopped = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.stopped = true));
    void this.usage.ensureLoaded();
    void this.load().then(() => this.cameBack());
  }

  protected readonly account = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.account : null;
  });

  /** The paid plans, cheapest first, as the catalogue publishes them. The trial is not sold. */
  protected readonly plans = computed(() => (this.catalogue()?.plans ?? []).filter((plan) => plan.trialDays === null));

  protected readonly packs = computed(() => this.catalogue()?.packs ?? []);

  /** Whether the account's credits are spent, which puts the two remedies at the top of the page. */
  protected readonly spent = computed(() => this.usage.allowance().kind === 'exhausted');

  protected readonly planStatus = computed<{ tone: CpStatusPillTone; label: string } | null>(() => {
    const account = this.account();
    const plan = account?.plan;
    if (!account || !plan) return null;

    const subscription = account.subscription;
    if (subscription?.status === 'PastDue') return { tone: 'warning', label: 'Payment overdue' };
    if (subscription?.status === 'Incomplete') return { tone: 'progress', label: 'Payment pending' };
    if (subscription?.cancelAt) return { tone: 'warning', label: `Ends ${this.date(subscription.cancelAt)}` };
    if (subscription?.nextPlanName && subscription.nextChangeAt) {
      return { tone: 'progress', label: `Moves to ${subscription.nextPlanName} on ${this.date(subscription.nextChangeAt)}` };
    }
    if (plan.isTrial && plan.trialEnded) return { tone: 'error', label: 'Trial ended' };
    if (plan.isTrial && plan.trialEndsAt) return { tone: 'neutral', label: `Trial ends ${this.date(plan.trialEndsAt)}` };
    return { tone: 'success', label: subscription ? 'Active' : 'On this plan' };
  });

  /** The plan in force as a line of facts — the server's figures, in words. */
  protected readonly planFacts = computed(() => {
    const account = this.account();
    const plan = account?.plan;
    if (!account || !plan) return [];

    const facts = [
      { label: 'Credits', value: `${this.number(plan.credits)} ${plan.period === 'Once' ? 'for the trial' : 'a month'}` },
      { label: 'Pictures', value: `Up to ${this.number(plan.dailyImageCap)} a day` },
    ];

    const subscription = account.subscription;
    if (subscription?.cycle) facts.push({ label: 'Billing', value: subscription.cycle === 'Annual' ? 'Yearly' : 'Monthly' });
    if (subscription?.currentPeriodEnd && !subscription.cancelAt) {
      facts.push({ label: 'Renews', value: this.date(subscription.currentPeriodEnd) });
    }
    if (account.packBalance > 0) facts.push({ label: 'Pack credits', value: this.number(account.packBalance) });

    return facts;
  });

  /** Why the plan cards cannot be chosen, or nothing when they can. */
  protected readonly plansUnavailable = computed(() => {
    const account = this.account();
    if (!account || account.paymentsAvailable) return '';
    return NOT_ON_OFFER;
  });

  /** Why the packs cannot be bought, or nothing when they can (B-61: an ended trial chooses a plan first). */
  protected readonly packsUnavailable = computed(() => {
    const account = this.account();
    if (!account || account.canBuyPacks) return '';
    return account.paymentsAvailable ? 'Choose a plan first — packs add credits to one.' : NOT_ON_OFFER;
  });

  // ---- the plan cards ------------------------------------------------------------------------------------

  protected priceOf(plan: Plan): string {
    return this.cycle() === 'Annual'
      ? `${this.money(plan.annualMonthlyPrice)} a month, ${this.money(plan.annualPrice)} a year`
      : `${this.money(plan.monthlyPrice)} a month`;
  }

  protected factsOf(plan: Plan): readonly CpOptionFact[] {
    return [
      { label: 'Price', value: this.priceOf(plan) },
      { label: 'Credits', value: `${this.number(plan.credits)} a month` },
      { label: 'Pictures', value: `Up to ${this.number(plan.dailyImageCap)} a day` },
      // Past three, the card moves them into its disclosure: what each quality costs, at the server's rate.
      ...(this.catalogue()?.pictureClasses ?? [])
        .filter((row) => plan.qualityClasses.includes(row.qualityClass))
        .map((row) => ({ label: `${row.qualityClass} picture`, value: `${this.number(row.perPicture)} credits` })),
    ];
  }

  /** Whether this card is the plan and cycle the account already pays on. */
  protected isCurrent(plan: Plan): boolean {
    const subscription = this.account()?.subscription;
    return subscription?.planKey === plan.key && subscription.cycle === this.cycle();
  }

  /** What pressing the card's button will do, by the server's rule: subscribe, upgrade now, switch or move later. */
  private changeOf(plan: Plan): 'subscribe' | 'upgrade' | 'switch-cycle' | 'move' {
    const account = this.account();
    if (!account?.subscription) return 'subscribe';
    if (account.subscription.planKey === plan.key) return 'switch-cycle';
    return account.upgradePlanKeys.includes(plan.key) ? 'upgrade' : 'move';
  }

  protected chooseLabel(plan: Plan): string {
    if (this.busy() === `plan:${plan.key}`) return 'Opening…';

    switch (this.changeOf(plan)) {
      case 'subscribe':
        return `Choose ${plan.name}`;
      case 'upgrade':
        return `Upgrade to ${plan.name}`;
      case 'switch-cycle':
        return `Switch to ${this.cycle() === 'Annual' ? 'yearly' : 'monthly'} billing`;
      case 'move':
        return `Move to ${plan.name}`;
    }
  }

  /** The paid plan the account's subscription is on, as the catalogue describes it; null without one. */
  private readonly currentPlan = computed(() => {
    const key = this.account()?.subscription?.planKey;
    return key ? (this.plans().find((plan) => plan.key === key) ?? null) : null;
  });

  protected async choosePlan(plan: Plan): Promise<void> {
    const account = this.account();
    if (!account || this.busy() !== null || !account.paymentsAvailable) return;

    const cycle = this.cycle();
    const change = this.changeOf(plan);

    if (change === 'subscribe') {
      return this.run(`plan:${plan.key}`, (key) => this.billing.startPlanCheckout(plan.key, cycle, key));
    }

    // Anything but an upgrade waits for the end of the paid period (B-54): said before, because nothing changes now.
    // An upgrade goes to the provider's page, which states the charge before anything is paid.
    if (change !== 'upgrade') {
      const current = this.currentPlan()?.name ?? 'your plan';
      const when = account.subscription?.currentPeriodEnd ? this.date(account.subscription.currentPeriodEnd) : 'the end of this period';
      const yearly = cycle === 'Annual' ? 'yearly' : 'monthly';
      const proceed = await this.confirm.confirm(
        change === 'switch-cycle'
          ? {
              title: `Switch to ${yearly} billing?`,
              message: `You stay on ${current}. From ${when} it is billed ${yearly}. Nothing is charged now.`,
              confirmLabel: `Switch to ${yearly} billing`,
              cancelLabel: 'Keep it as it is',
              tone: 'neutral',
            }
          : {
              title: `Move to ${plan.name}?`,
              message: `You keep ${current} until ${when}, then move to ${plan.name}. Nothing is charged now.`,
              confirmLabel: `Move to ${plan.name}`,
              cancelLabel: `Keep ${current}`,
              tone: 'neutral',
            },
      );
      if (!proceed) return;
    }

    return this.run(`plan:${plan.key}`, (key) => this.billing.changePlan(plan.key, cycle, key));
  }

  protected async buyPack(pack: CreditPack): Promise<void> {
    const account = this.account();
    if (!account || this.busy() !== null || !account.canBuyPacks) return;

    return this.run(`pack:${pack.key}`, (key) => this.billing.startPackCheckout(pack.key, key));
  }

  protected packHeading(pack: CreditPack): string {
    return `${this.number(pack.credits)} credits`;
  }

  protected packLabel(pack: CreditPack): string {
    return this.busy() === `pack:${pack.key}` ? 'Opening…' : `Buy ${this.number(pack.credits)} credits`;
  }

  protected packFacts(pack: CreditPack): readonly CpOptionFact[] {
    return [{ label: 'Price', value: this.money(pack.price) }];
  }

  protected async openPortal(): Promise<void> {
    if (this.busy() !== null) return;
    await this.run('portal', () => this.billing.openPortal());
  }

  protected async withdrawChange(): Promise<void> {
    if (this.busy() !== null) return;

    this.busy.set('withdraw');
    try {
      const outcome = await this.billing.withdrawPlanChange(this.keyFor('withdraw'));
      if (outcome.status !== 'unavailable') this.keys.delete('withdraw');

      if (outcome.status === 'done') {
        this.tell({ tone: 'success', text: 'The change is withdrawn. Your plan carries on as it is.' });
        await this.load();
        // The button that was pressed is gone with the change: focus goes to the card it was in.
        this.focusPlace(PLAN_PLACE);
        return;
      }

      await this.refused(outcome);
    } finally {
      this.busy.set(null);
    }
  }

  protected async setNoticeEnabled(enabled: boolean): Promise<void> {
    if (this.savingNotice()) return;

    this.noticeEnabled.set(enabled);
    this.savingNotice.set(true);
    try {
      if (!(await this.usage.setLowCreditNoticeEnabled(enabled))) {
        this.noticeEnabled.set(!enabled);
        this.refusal.set('We couldn’t save that setting just now. Try again.');
      }
    } finally {
      this.savingNotice.set(false);
    }
  }

  protected setCycle(cycle: BillingCycle): void {
    this.cycle.set(cycle);
  }

  /** Reads the account again while the error card stays where it is, so the button pressed keeps focus. */
  protected async retry(): Promise<void> {
    if (this.retrying()) return;

    this.retrying.set(true);
    try {
      await this.load();
    } finally {
      this.retrying.set(false);
    }
  }

  protected retryCatalogue(): void {
    this.catalogueAsk.update((asks) => asks + 1);
  }

  /** Takes the creator to a part of the page — scrolled into view and focused, so a keyboard lands there too. */
  protected goTo(place: string): void {
    this.focusPlace(place);
  }

  protected async refresh(): Promise<void> {
    this.news.set(null);
    await Promise.all([this.load(), this.usage.refresh()]);
  }

  // ---- the commands --------------------------------------------------------------------------------------

  /** Runs one command under its own key, and goes to the provider's page or says what happened. */
  private async run(what: string, command: (key: string) => Promise<BillingCommandOutcome>): Promise<void> {
    this.busy.set(what);
    this.refusal.set(null);

    try {
      const outcome = await command(this.keyFor(what));

      if (outcome.status === 'redirect' && outcome.redirect.url !== null) {
        // Kept until the page is left, so a back-button return and a second press answer with the same page.
        this.billing.leaveFor(outcome.redirect.url);
        return;
      }

      // A definite answer ends the command, so the next press is a new one. "Unavailable" is not definite — the
      // request may have landed — so its key is kept, and a retry is answered with whatever the first one did.
      if (outcome.status !== 'unavailable') this.keys.delete(what);

      if (outcome.status === 'redirect' && outcome.redirect.scheduled) {
        this.tell({ tone: 'success', text: 'Done. This takes effect when your current period ends — nothing is charged now.' });
        await this.load();
        return;
      }

      await this.refused(outcome);
    } finally {
      this.busy.set(null);
    }
  }

  private async refused(outcome: BillingCommandOutcome): Promise<void> {
    if (outcome.status === 'refused') {
      this.refusal.set(outcome.message);
      // The page offered something its account no longer fits: read it again, so the right button is offered.
      if (outcome.reread) await this.load();
      return;
    }

    if (outcome.status === 'unavailable') this.refusal.set(UNAVAILABLE);
  }

  private keyFor(what: string): string {
    let key = this.keys.get(what);
    if (!key) {
      key = crypto.randomUUID();
      this.keys.set(what, key);
    }
    return key;
  }

  // ---- reading, and coming back from the provider -------------------------------------------------------------

  private async load(): Promise<void> {
    const account = await this.billing.read();
    this.state.set(account === null ? { kind: 'error' } : { kind: 'ready', account });
  }

  /** What the provider's return URL says happened, said once, then taken off the address. */
  private async cameBack(): Promise<void> {
    const params = this.route.snapshot.queryParamMap;
    const checkout = params.get('checkout');
    const session = params.get('session_id');
    if (checkout === null) return;

    void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });

    if (checkout === 'cancelled') {
      this.tell({ tone: 'neutral', text: 'Checkout was closed. Nothing was charged.' });
      return;
    }

    if (checkout === 'upgrade') {
      this.tell({ tone: 'neutral', text: 'Your upgrade is being confirmed. It shows here once Stripe tells us — refresh in a moment.' });
      return;
    }

    if ((checkout === 'plan' || checkout === 'pack') && session !== null) {
      await this.waitFor(session, checkout === 'plan' ? 'Plan' : 'Pack');
    }
  }

  /** Asks until the webhook has landed, or until it is plain the creator should look again later. */
  private async waitFor(session: string, kind: 'Plan' | 'Pack'): Promise<void> {
    if (this.polling) return;
    this.polling = true;
    this.tell({ tone: 'neutral', text: 'Finishing your purchase…' });

    for (let attempt = 0; attempt < CHECKOUT_POLL_TRIES && !this.stopped; attempt++) {
      const status = await this.billing.checkout(session, kind);

      if (status?.state === 'Complete') {
        this.tell({
          tone: 'success',
          text:
            kind === 'Plan'
              ? `${status.planName ?? 'Your plan'} is active.`
              : `${this.number(status.credits ?? 0)} credits added.`,
        });
        await Promise.all([this.load(), this.usage.refresh()]);
        this.polling = false;
        return;
      }

      await new Promise((resolve) => setTimeout(resolve, CHECKOUT_POLL_MS));
    }

    this.polling = false;
    if (!this.stopped) {
      this.tell({ tone: 'neutral', text: 'This is taking longer than usual. It can take a minute to show here — refresh in a moment.' });
    }
  }

  private tell(news: PageNews): void {
    this.refusal.set(null);
    this.news.set(news);
  }

  private focusPlace(place: string): void {
    const target = this.document.getElementById(place);
    target?.scrollIntoView({ block: 'start' });
    target?.focus({ preventScroll: true });
  }

  // ---- words ------------------------------------------------------------------------------------------------

  private date(iso: string): string {
    return formatAllowanceReset(iso, null) ?? 'soon';
  }

  private number(value: number): string {
    return value.toLocaleString();
  }

  private money(amount: number): string {
    const currency = this.catalogue()?.currency ?? '';
    try {
      return currency ? new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(amount) : String(amount);
    } catch {
      return `${amount} ${currency}`;
    }
  }
}
