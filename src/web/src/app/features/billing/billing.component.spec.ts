import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { BillingAccount } from '../../models/billing.models';
import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { BillingCommandOutcome, BillingService } from '../../services/billing.service';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { BillingComponent, TAX_LINE } from './billing.component';

const plan = (key: string, name: string, monthlyPrice: number, credits: number, trialDays: number | null = null) => ({
  key,
  name,
  monthlyPrice,
  annualMonthlyPrice: monthlyPrice - 3,
  annualPrice: (monthlyPrice - 3) * 12,
  annualSaving: 36,
  credits,
  exampleMix: '',
  dailyImageCap: 25,
  qualityClasses: ['Draft' as const, 'Standard' as const],
  recommended: false,
  trialDays,
  trialImageCap: trialDays === null ? null : 10,
  picturesAtDefaultQuality: null,
  exampleCounts: null,
});

const catalogue: PlanCatalogue = {
  currency: 'USD',
  plans: [plan('trial', 'Trial', 0, 1000, 14), plan('creator', 'Creator', 12, 4500), plan('pro', 'Pro', 49, 18000)],
  packs: [{ key: 'small', credits: 2500, price: 10 }],
  rates: { textDraft: 5, longFormSection: 10 },
  pictureClasses: [
    { qualityClass: 'Draft', perPicture: 25 },
    { qualityClass: 'Standard', perPicture: 50 },
  ],
  defaultPictureClass: 'Standard',
};

function account(overrides: Partial<BillingAccount> = {}): BillingAccount {
  return {
    paymentsAvailable: true,
    isTestMode: false,
    plan: {
      key: 'trial',
      name: 'Trial',
      credits: 1000,
      period: 'Once',
      dailyImageCap: 5,
      isTrial: true,
      trialEndsAt: '2026-11-12T09:00:00+00:00',
      trialEnded: false,
    },
    subscription: null,
    packBalance: 0,
    canSubscribe: true,
    canBuyPacks: true,
    upgradePlanKeys: [],
    ...overrides,
  };
}

const subscribed = (planKey: string, extra: Partial<NonNullable<BillingAccount['subscription']>> = {}) =>
  account({
    plan: {
      key: planKey,
      name: planKey === 'pro' ? 'Pro' : 'Creator',
      credits: 4500,
      period: 'Monthly',
      dailyImageCap: 25,
      isTrial: false,
      trialEndsAt: null,
      trialEnded: false,
    },
    canSubscribe: false,
    // The server's rule, sent: from Creator, Pro is an upgrade; from Pro, nothing is.
    upgradePlanKeys: planKey === 'creator' ? ['pro'] : [],
    subscription: {
      status: 'Active',
      planKey,
      cycle: 'Monthly',
      currentPeriodEnd: '2026-12-01T00:00:00+00:00',
      cancelAt: null,
      nextPlanKey: null,
      nextPlanName: null,
      nextCycle: null,
      nextChangeAt: null,
      ...extra,
    },
  });

describe('BillingComponent', () => {
  let fixture: ComponentFixture<BillingComponent>;
  let host: HTMLElement;
  let billing: jasmine.SpyObj<BillingService>;
  let confirm: jasmine.SpyObj<ConfirmService>;
  let allowanceKind: ReturnType<typeof signal<string>>;

  async function create(
    read: BillingAccount | null,
    query: Record<string, string> = {},
    before: () => void = () => undefined,
    published: PlanCatalogue | null = catalogue,
  ): Promise<void> {
    billing = jasmine.createSpyObj<BillingService>('BillingService', [
      'read',
      'startPlanCheckout',
      'startPackCheckout',
      'changePlan',
      'withdrawPlanChange',
      'openPortal',
      'checkout',
      'leaveFor',
    ]);
    billing.read.and.resolveTo(read);
    confirm = jasmine.createSpyObj<ConfirmService>('ConfirmService', ['confirm']);
    confirm.confirm.and.resolveTo(true);
    allowanceKind = signal('healthy');
    before();

    TestBed.configureTestingModule({
      imports: [BillingComponent],
      providers: [
        provideRouter([{ path: 'app/billing', component: BillingComponent }]),
        { provide: BillingService, useValue: billing },
        { provide: ConfirmService, useValue: confirm },
        { provide: PlanCatalogueService, useValue: { read: () => of(published) } },
        {
          provide: AiUsageService,
          useValue: {
            allowance: () => ({ kind: allowanceKind() }),
            lowCreditNoticeEnabled: () => true,
            ensureLoaded: () => Promise.resolve(),
            refresh: () => Promise.resolve(),
            setLowCreditNoticeEnabled: jasmine.createSpy('setLowCreditNoticeEnabled').and.resolveTo(true),
          },
        },
      ],
    });

    await TestBed.inject(Router).navigate(['/app/billing'], { queryParams: query });
    fixture = TestBed.createComponent(BillingComponent);
    host = fixture.nativeElement;
    await settle();
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  const text = () => host.textContent?.replace(/\s+/g, ' ') ?? '';
  const buttonNamed = (name: string) =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) => button.textContent?.trim() === name);
  const alert = () => host.querySelector('[role="alert"]')?.textContent?.trim() ?? '';
  const status = () => Array.from(host.querySelectorAll('cp-notice[role="status"]')).map((node) => node.textContent?.trim()).join(' ');

  async function press(name: string): Promise<void> {
    const button = buttonNamed(name);
    expect(button).withContext(`a button named "${name}"`).toBeDefined();
    button!.click();
    await settle();
  }

  const redirect = (url: string): BillingCommandOutcome => ({ status: 'redirect', redirect: { url, scheduled: false } });

  it('shows a trial, the paid plans to choose from with the tax line, and no portal for an account that has never paid', async () => {
    await create(account());

    expect(text()).toContain('Trial');
    expect(text()).toContain('Trial ends');
    expect(buttonNamed('Choose Creator')).toBeDefined();
    expect(buttonNamed('Choose Pro')).toBeDefined();
    // The trial is not sold.
    expect(buttonNamed('Choose Trial')).toBeUndefined();
    expect(text()).toContain(TAX_LINE);
    expect(buttonNamed('Manage payment and invoices')).toBeUndefined();
  });

  it('starts Checkout for a first plan on the cycle shown, and leaves for the provider’s page', async () => {
    await create(account());
    billing.startPlanCheckout.and.resolveTo(redirect('https://checkout.stripe.test/c/1'));

    await press('Yearly');
    await press('Choose Creator');

    expect(billing.startPlanCheckout).toHaveBeenCalledWith('creator', 'Annual', jasmine.any(String));
    expect(billing.leaveFor).toHaveBeenCalledWith('https://checkout.stripe.test/c/1');
  });

  it('prices each card from the catalogue on the cycle chosen, and adds nothing up itself', async () => {
    await create(account());

    expect(text()).toContain('$12.00 a month');
    await press('Yearly');
    expect(text()).toContain('$9.00 a month, $108.00 a year');
  });

  it('upgrades at once on the provider’s page, and asks before a move that waits for the period’s end', async () => {
    await create(subscribed('creator'));
    billing.changePlan.and.resolveTo(redirect('https://billing.stripe.test/upgrade'));

    await press('Upgrade to Pro');
    expect(confirm.confirm).not.toHaveBeenCalled();
    expect(billing.changePlan).toHaveBeenCalledWith('pro', 'Monthly', jasmine.any(String));

    // The plan in force is marked chosen rather than offered again.
    expect(text()).toContain('Chosen');
  });

  it('says a move to a lower plan is scheduled, with nothing charged, once confirmed', async () => {
    await create(subscribed('pro'));
    billing.changePlan.and.resolveTo({ status: 'redirect', redirect: { url: null, scheduled: true } });

    await press('Move to Creator');

    expect(confirm.confirm).toHaveBeenCalled();
    expect(billing.leaveFor).not.toHaveBeenCalled();
    expect(status()).toContain('nothing is charged now');
  });

  it('does nothing when the move is not confirmed', async () => {
    await create(subscribed('pro'));
    confirm.confirm.and.resolveTo(false);

    await press('Move to Creator');

    expect(billing.changePlan).not.toHaveBeenCalled();
  });

  it('offers to keep the current plan when a change is scheduled, and withdraws it', async () => {
    await create(subscribed('pro', { nextPlanKey: 'creator', nextPlanName: 'Creator', nextChangeAt: '2026-12-01T00:00:00+00:00' }));
    billing.withdrawPlanChange.and.resolveTo({ status: 'done' });

    expect(text()).toContain('Moves to Creator on');
    await press('Keep my current plan');

    expect(billing.withdrawPlanChange).toHaveBeenCalled();
    expect(status()).toContain('withdrawn');
    // The button pressed is gone with the change, so focus goes to the card it was in.
    expect(document.activeElement?.id).toBe('billing-plan');
  });

  it('opens the provider’s portal for an account that pays', async () => {
    await create(subscribed('creator'));
    billing.openPortal.and.resolveTo(redirect('https://billing.stripe.test/p/1'));

    await press('Manage payment and invoices');

    expect(billing.leaveFor).toHaveBeenCalledWith('https://billing.stripe.test/p/1');
  });

  it('buys a pack through Checkout', async () => {
    await create(account());
    billing.startPackCheckout.and.resolveTo(redirect('https://checkout.stripe.test/c/pack'));

    await press('Buy 2,500 credits');

    expect(billing.startPackCheckout).toHaveBeenCalledWith('small', jasmine.any(String));
    expect(billing.leaveFor).toHaveBeenCalledWith('https://checkout.stripe.test/c/pack');
  });

  it('keeps the same key for a retry of one purchase, so the server answers with the same page', async () => {
    await create(account());
    billing.startPackCheckout.and.resolveTo({ status: 'unavailable' });

    await press('Buy 2,500 credits');
    const first = billing.startPackCheckout.calls.mostRecent().args[1];
    billing.startPackCheckout.and.resolveTo(redirect('https://checkout.stripe.test/c/pack'));
    await press('Buy 2,500 credits');

    // "Unavailable" may have landed, so the retry is the same request: the server answers it once.
    expect(billing.startPackCheckout.calls.mostRecent().args[1]).toBe(first);
  });

  it('asks an ended trial to choose a plan before any pack', async () => {
    await create(
      account({
        canBuyPacks: false,
        plan: { ...account().plan!, trialEnded: true },
      }),
    );

    expect(text()).toContain('Trial ended');
    expect(text()).toContain('Choose a plan first');
    expect(buttonNamed('Buy 2,500 credits')?.getAttribute('aria-disabled')).toBe('true');
  });

  it('puts both remedies first when the credits are spent, and each takes focus to its card', async () => {
    await create(account());
    allowanceKind.set('exhausted');
    await settle();

    expect(text()).toContain('Your AI credits are spent.');
    await press('Buy credits');
    expect(document.activeElement?.id).toBe('billing-packs');
    await press('Choose a plan');
    expect(document.activeElement?.id).toBe('billing-plans');
  });

  it('says the server’s refusal and reads the page again when it is out of date', async () => {
    await create(account());
    billing.startPlanCheckout.and.resolveTo({
      status: 'refused',
      code: 'billing.checkout.subscribed.conflict',
      message: 'You already have a plan. Change it from this page instead.',
      reread: true,
    });

    await press('Choose Creator');

    expect(alert()).toContain('You already have a plan');
    expect(billing.read).toHaveBeenCalledTimes(2);
    expect(billing.leaveFor).not.toHaveBeenCalled();
  });

  it('shows the plans and packs not on offer, says why, and buys nothing, where payments are not available', async () => {
    await create(account({ paymentsAvailable: false, canSubscribe: false, canBuyPacks: false }));

    expect(text()).toContain('nothing can be bought here');
    expect(buttonNamed('Choose Creator')?.getAttribute('aria-disabled')).toBe('true');
    expect(buttonNamed('Buy 2,500 credits')?.getAttribute('aria-disabled')).toBe('true');
    await press('Choose Creator');
    await press('Buy 2,500 credits');

    expect(billing.startPlanCheckout).not.toHaveBeenCalled();
    expect(billing.startPackCheckout).not.toHaveBeenCalled();
  });

  it('switching only how often a plan is paid is said as that, and confirmed as that', async () => {
    await create(subscribed('creator'));
    billing.changePlan.and.resolveTo({ status: 'redirect', redirect: { url: null, scheduled: true } });

    await press('Yearly');
    await press('Switch to yearly billing');

    expect(confirm.confirm.calls.mostRecent().args[0].title).toBe('Switch to yearly billing?');
    expect(billing.changePlan).toHaveBeenCalledWith('creator', 'Annual', jasmine.any(String));
  });

  it('keeps the cycle the creator picked through a re-read of the account', async () => {
    await create(subscribed('pro', { nextPlanKey: 'creator', nextPlanName: 'Creator', nextChangeAt: '2026-12-01T00:00:00+00:00' }));
    billing.withdrawPlanChange.and.resolveTo({ status: 'done' });

    await press('Yearly');
    await press('Keep my current plan');

    expect(text()).toContain('$9.00 a month, $108.00 a year');
  });

  it('says the plans could not be read rather than that there are none, and reads them again', async () => {
    await create(account(), {}, () => undefined, null);

    expect(text()).toContain('We couldn’t load the plans just now.');
    expect(text()).not.toContain('No plans are on offer');
  });

  it('says payments are in test mode', async () => {
    await create(account({ isTestMode: true }));

    expect(text()).toContain('test mode');
  });

  it('offers to try again when the billing could not be read', async () => {
    await create(null);

    expect(text()).toContain('Couldn’t load');
    billing.read.and.resolveTo(account());
    buttonNamed('Try again')!.focus();
    await press('Try again');

    expect(buttonNamed('Choose Creator')).toBeDefined();
  });

  it('back from a pack’s Checkout, waits for the webhook and then says what was added', async () => {
    await create(account(), { checkout: 'pack', session_id: 'cs_test_1' }, () =>
      billing.checkout.and.resolveTo({ state: 'Complete', planName: null, credits: 2500 }),
    );

    expect(billing.checkout).toHaveBeenCalledWith('cs_test_1', 'Pack');
    expect(status()).toContain('2,500 credits added.');
  });

  it('back from a closed Checkout, says nothing was charged', async () => {
    await create(account(), { checkout: 'cancelled' });

    expect(status()).toContain('Nothing was charged.');
  });
});
