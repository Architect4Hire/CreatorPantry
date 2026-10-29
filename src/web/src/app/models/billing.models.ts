// Wire models for /api/v1/me/billing (AP.6.5). Mirrors
// CreatorPantry.Domain/Modules/Billing/Managers/BillingAccountModels.cs.
//
// Every figure and every decision here is the server's: which plan is in force, whether a trial has ended, what may
// be bought. The page formats them and decides nothing — a button the server says is not on offer is not shown as
// one that is.

import { decodeEnum, isRecord } from './recipe.models';

/** Mirrors `BillingCycle`: how often a plan is paid for. */
export type BillingCycle = 'Monthly' | 'Annual';

const BILLING_CYCLE_VALUES: ReadonlySet<string> = new Set<BillingCycle>(['Monthly', 'Annual']);

/** Mirrors `BillingSubscriptionStatus`, as the provider last reported it. */
export type BillingSubscriptionStatus =
  | 'Incomplete'
  | 'IncompleteExpired'
  | 'Trialing'
  | 'Active'
  | 'PastDue'
  | 'Unpaid'
  | 'Canceled'
  | 'Paused';

const SUBSCRIPTION_STATUS_VALUES: ReadonlySet<string> = new Set<BillingSubscriptionStatus>([
  'Incomplete',
  'IncompleteExpired',
  'Trialing',
  'Active',
  'PastDue',
  'Unpaid',
  'Canceled',
  'Paused',
]);

/** The plan in force, as the billing page states it. */
export interface BillingAccountPlan {
  readonly key: string;
  readonly name: string;
  readonly credits: number;
  readonly period: 'Monthly' | 'Once';
  readonly dailyImageCap: number;
  readonly isTrial: boolean;
  /** When the trial ends or ended (AP.7.1); null on a paid plan. */
  readonly trialEndsAt: string | null;
  readonly trialEnded: boolean;
}

/** The account's live subscription. */
export interface BillingAccountSubscription {
  readonly status: BillingSubscriptionStatus;
  readonly planKey: string | null;
  readonly cycle: BillingCycle | null;
  readonly currentPeriodEnd: string | null;
  readonly cancelAt: string | null;
  /** A change already scheduled for the end of the period (B-54); null when none is. */
  readonly nextPlanKey: string | null;
  readonly nextPlanName: string | null;
  readonly nextCycle: BillingCycle | null;
  readonly nextChangeAt: string | null;
}

/** Mirrors `BillingAccountServiceModel`. */
export interface BillingAccount {
  readonly paymentsAvailable: boolean;
  readonly isTestMode: boolean;
  readonly plan: BillingAccountPlan | null;
  readonly subscription: BillingAccountSubscription | null;
  readonly packBalance: number;
  readonly canSubscribe: boolean;
  readonly canBuyPacks: boolean;
  /**
   * The paid plans a change to would land at once, paying the difference now (B-56); any other waits for the end of
   * the paid period (B-54). The server's rule — the page names each button by it and works nothing out.
   */
  readonly upgradePlanKeys: readonly string[];
}

/** Where the creator goes next: the provider's page, or nowhere because a change is scheduled. */
export interface BillingRedirect {
  readonly url: string | null;
  readonly scheduled: boolean;
}

/** Whether what a Checkout bought has landed (the webhook decides, never the redirect). */
export interface BillingCheckoutStatus {
  readonly state: 'Pending' | 'Complete';
  readonly planName: string | null;
  readonly credits: number | null;
}

function text(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function figure(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function decodePlan(value: unknown): BillingAccountPlan | null {
  if (!isRecord(value)) return null;

  const key = text(value['key']);
  const name = text(value['name']);
  const credits = figure(value['credits']);
  const dailyImageCap = figure(value['dailyImageCap']);
  const period = value['period'] === 'Once' ? 'Once' : value['period'] === 'Monthly' ? 'Monthly' : null;

  if (key === null || name === null || credits === null || dailyImageCap === null || period === null) return null;

  return {
    key,
    name,
    credits,
    period,
    dailyImageCap,
    isTrial: value['isTrial'] === true,
    trialEndsAt: text(value['trialEndsAt']),
    trialEnded: value['trialEnded'] === true,
  };
}

function decodeSubscription(value: unknown): BillingAccountSubscription | null {
  if (!isRecord(value)) return null;

  const status = decodeEnum<BillingSubscriptionStatus>(SUBSCRIPTION_STATUS_VALUES, value['status']);
  if (status === null) return null;

  return {
    status,
    planKey: text(value['planKey']),
    cycle: decodeEnum<BillingCycle>(BILLING_CYCLE_VALUES, value['cycle']),
    currentPeriodEnd: text(value['currentPeriodEnd']),
    cancelAt: text(value['cancelAt']),
    nextPlanKey: text(value['nextPlanKey']),
    nextPlanName: text(value['nextPlanName']),
    nextCycle: decodeEnum<BillingCycle>(BILLING_CYCLE_VALUES, value['nextCycle']),
    nextChangeAt: text(value['nextChangeAt']),
  };
}

/** Decodes the billing read, or null when it is not one. */
export function decodeBillingAccount(value: unknown): BillingAccount | null {
  if (!isRecord(value)) return null;

  const packBalance = figure(value['packBalance']);
  if (packBalance === null || typeof value['paymentsAvailable'] !== 'boolean') return null;

  return {
    paymentsAvailable: value['paymentsAvailable'],
    isTestMode: value['isTestMode'] === true,
    plan: value['plan'] === null ? null : decodePlan(value['plan']),
    subscription: value['subscription'] === null ? null : decodeSubscription(value['subscription']),
    packBalance,
    canSubscribe: value['canSubscribe'] === true,
    canBuyPacks: value['canBuyPacks'] === true,
    upgradePlanKeys: Array.isArray(value['upgradePlanKeys'])
      ? value['upgradePlanKeys'].filter((key): key is string => typeof key === 'string')
      : [],
  };
}

/** Decodes a redirect, or null: a page to go to must be an absolute https URL, or there must be none. */
export function decodeBillingRedirect(value: unknown): BillingRedirect | null {
  if (!isRecord(value)) return null;

  const url = text(value['url']);
  const scheduled = value['scheduled'] === true;

  if (url === null) return scheduled ? { url: null, scheduled } : null;

  try {
    return new URL(url).protocol === 'https:' ? { url, scheduled } : null;
  } catch {
    return null;
  }
}

/** Decodes a Checkout's state, or null. */
export function decodeBillingCheckoutStatus(value: unknown): BillingCheckoutStatus | null {
  if (!isRecord(value)) return null;

  const state = value['state'] === 'Complete' ? 'Complete' : value['state'] === 'Pending' ? 'Pending' : null;
  if (state === null) return null;

  return { state, planName: text(value['planName']), credits: figure(value['credits']) };
}
