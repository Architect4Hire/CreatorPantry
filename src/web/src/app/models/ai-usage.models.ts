import { AiTaskType, AI_TASK_TYPE_VALUES } from './ai-proposal.models';
import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';

/** Mirrors `AiQuotaUnit`. */
export type AiQuotaUnit = 'Unspecified' | 'Credits' | 'Tokens';

const AI_QUOTA_UNIT_VALUES: ReadonlySet<string> = new Set<AiQuotaUnit>(['Unspecified', 'Credits', 'Tokens']);

/** Mirrors `AiQuotaPeriodLength`. */
export type AiQuotaPeriodLength = 'Unspecified' | 'Daily' | 'Weekly' | 'Monthly';

const AI_QUOTA_PERIOD_LENGTH_VALUES: ReadonlySet<string> = new Set<AiQuotaPeriodLength>([
  'Unspecified',
  'Daily',
  'Weekly',
  'Monthly',
]);

/**
 * One allowance period's totals, in the unit the period was denominated in. Mirrors
 * `AccountAiUsagePeriodServiceModel`.
 */
export interface AccountAiUsagePeriod {
  readonly unit: AiQuotaUnit;
  /** The period's spendable total: its own allowance plus anything carried into it. */
  readonly allowance: number;
  /** How much of `allowance` rolled in from the period before, so a roll is visible rather than implied. */
  readonly carriedOver: number;
  /** What settled runs actually charged. */
  readonly consumed: number;
  /** What runs in flight are holding right now. */
  readonly reserved: number;
  /** `allowance` less `consumed` and `reserved` — the figure an admission judges the next request against. */
  readonly remaining: number;
  readonly periodLength: AiQuotaPeriodLength;
  /**
   * The IANA zone the two boundaries below were computed in.
   *
   * <strong>Render `resetsAt` in this zone, never the browser's.</strong> The boundaries are local midnights,
   * so a creator whose allowance runs on a different calendar would be shown the wrong day.
   */
  readonly timeZoneId: string;
  /** When the period opened. Inclusive. */
  readonly startsAt: string;
  /** When it ends and the allowance comes back. Exclusive. */
  readonly resetsAt: string;
}

/** What one capability charged this period. Mirrors `AccountAiUsageTaskTotalServiceModel`. */
export interface AccountAiUsageTaskTotal {
  readonly taskType: AiTaskType;
  readonly amount: number;
  /** How many provider attempts it made, billable or not. */
  readonly requests: number;
}

/** What one workspace's work charged this period. Mirrors `AccountAiUsageWorkspaceTotalServiceModel`. */
export interface AccountAiUsageWorkspaceTotal {
  /** Null only for spend the server could not place in a workspace at all. */
  readonly workspaceId: string | null;
  /**
   * The workspace's name, <strong>or null when the account no longer holds active membership there</strong>.
   * The total stays and the name goes: the spend is the creator's own history, and the name is not theirs to
   * have any more. Render the row, never drop it, or the parts stop summing to the whole.
   */
  readonly workspaceName: string | null;
  readonly amount: number;
  readonly requests: number;
}

/** The plan the account is on, as its usage page names it. Mirrors `AccountAiUsagePlanServiceModel`. */
export interface AccountAiUsagePlan {
  readonly key: string;
  readonly name: string;
  /**
   * Whether this period's allowance comes back when it resets — <strong>false for a trial's credits, which are
   * granted once</strong> (B-48). The server's answer: never promise "comes back on…" when this is false.
   */
  readonly allowanceRenews: boolean;
}

/**
 * The plan's pictures for the UTC day. Mirrors `AccountAiUsagePicturesTodayServiceModel` (DL.9).
 *
 * <strong>Every figure is the server's</strong>: `left` is sent rather than worked out from `cap` and `used`, so a
 * page that shows it never subtracts. A snapshot — admission counts again and decides.
 */
export interface AccountAiUsagePicturesToday {
  readonly cap: number;
  readonly used: number;
  readonly left: number;
  /** The next UTC midnight. Shown in UTC: the day is UTC's, whatever the creator's own zone. */
  readonly resetsAt: string;
}

/** A trial's pictures in all, which never turn over. Mirrors `AccountAiUsageTrialPicturesServiceModel` (DL.9). */
export interface AccountAiUsageTrialPictures {
  readonly cap: number;
  readonly used: number;
  readonly left: number;
}

/**
 * Where the account stands with the low-credit notice. Mirrors `AccountAiLowCreditServiceModel` (B-59).
 *
 * Facts, not a decision about what to draw: whether the notice is shown is the app's call, and
 * `AiUsageService.lowCredit` is the one place that makes it.
 */
export interface AccountAiLowCredit {
  /** Whether an admission this period left the account at or below the server's low-credit line. */
  readonly isNoticed: boolean;
  /** Whether the creator has put this period's notice away. False again when the period rolls. */
  readonly isDismissed: boolean;
  /** Whether the account wants the notice at all. True until it says otherwise. */
  readonly noticeEnabled: boolean;
  /** What the account can actually spend: `period.remaining` plus `packRemaining`, summed by the server. */
  readonly spendable: number;
}

/** The signed-in account's allowance right now, and what it has gone on. Mirrors `AccountAiUsageServiceModel`. */
export interface AccountAiUsage {
  readonly period: AccountAiUsagePeriod;
  /** AI access switched off for this account, regardless of what `period` says is left. */
  readonly isSuspended: boolean;
  readonly byTask: readonly AccountAiUsageTaskTotal[];
  readonly byWorkspace: readonly AccountAiUsageWorkspaceTotal[];
  /**
   * Whether what is left is nearly spent — <strong>the server's line, not this bundle's</strong> (USAGE-010,
   * AP.2.6). The threshold lives in `Ai:Quota:NearlySpentFraction`, so every surface that warns agrees with the
   * image quote's `leavesNearlySpent` without keeping a copy of it.
   */
  readonly isNearlySpent: boolean;
  /** What pictures charged this period. With `writing`, makes up `period.consumed`. */
  readonly pictures: number;
  /** What every other kind of AI help charged this period. */
  readonly writing: number;
  /**
   * The account's credit packs, which never expire (B-50) and are spent after the allowance (AP.5.2).
   * <strong>Beside `period.remaining`, never added to it</strong> — the same figure a quote and a refusal call
   * `packRemaining`.
   */
  readonly packRemaining: number;
  /** The plan the account is on; null on a host with no plan catalogue. */
  readonly plan: AccountAiUsagePlan | null;
  /** Where the account stands with the low-credit notice (B-59). */
  readonly lowCredit: AccountAiLowCredit;
  /**
   * The plan's pictures today. Null when no daily cap is enforced, and when the account can make nothing at all —
   * suspended, or a trial that has ended — so there is nothing to count down.
   */
  readonly picturesToday: AccountAiUsagePicturesToday | null;
  /** A trial's pictures in all; null on a paid plan, and when `picturesToday` is null for the same reason. */
  readonly trialPictures: AccountAiUsageTrialPictures | null;
}

/**
 * Decodes `GET /api/v1/me/ai-usage`, or null when the payload is not one.
 *
 * Enums arrive as named string members — the API registers a global `JsonStringEnumConverter`, and the shape
 * is pinned by the committed OpenAPI snapshot. An unknown member decodes to null and fails the whole read
 * loudly rather than quietly widening to tolerate a format the backend was never proven to send.
 */
export function decodeAccountAiUsage(value: unknown): AccountAiUsage | null {
  if (!isRecord(value)) return null;

  const period = decodePeriod(value['period']);
  const isSuspended = value['isSuspended'];
  const byTask = decodeList(value['byTask'], decodeTaskTotal);
  const byWorkspace = decodeList(value['byWorkspace'], decodeWorkspaceTotal);
  const isNearlySpent = value['isNearlySpent'];
  const pictures = amountOf(value['pictures']);
  const writing = amountOf(value['writing']);
  const packRemaining = amountOf(value['packRemaining']);
  const plan = decodePlan(value['plan']);
  const lowCredit = decodeLowCredit(value['lowCredit']);
  const picturesToday = decodePictureCount(value['picturesToday'], true);
  const trialPictures = decodePictureCount(value['trialPictures'], false);

  if (
    period === null ||
    typeof isSuspended !== 'boolean' ||
    typeof isNearlySpent !== 'boolean' ||
    byTask === null ||
    byWorkspace === null ||
    pictures === null ||
    writing === null ||
    // Required, though the schema marks it optional: the server always sends it, and a missing balance read as
    // none would lock a creator out of credits they hold.
    packRemaining === null ||
    plan === undefined ||
    lowCredit === null
  ) {
    return null;
  }

  return {
    period,
    isSuspended,
    byTask,
    byWorkspace,
    isNearlySpent,
    pictures,
    writing,
    packRemaining,
    plan,
    lowCredit,
    // A count that cannot be read is said as none rather than failing the read: the allowance, the plan and the
    // breakdowns beside it are still true, and the server still decides at the request (DESIGN-SYSTEM.md, "Meters
    // and figures"). Nothing is blocked by a missing count.
    picturesToday: picturesToday ?? null,
    trialPictures: trialPictures ?? null,
  };
}

/**
 * A picture count — today's when `withReset`, a trial's otherwise — null when the server sends none, or undefined
 * when what arrived is not one. Absent reads as null: a server from before DL.9 sends neither.
 */
function decodePictureCount(
  value: unknown,
  withReset: true,
): AccountAiUsagePicturesToday | null | undefined;
function decodePictureCount(
  value: unknown,
  withReset: false,
): AccountAiUsageTrialPictures | null | undefined;
function decodePictureCount(
  value: unknown,
  withReset: boolean,
): AccountAiUsagePicturesToday | AccountAiUsageTrialPictures | null | undefined {
  if (value === null || value === undefined) return null;
  if (!isRecord(value)) return undefined;

  const cap = countOf(value['cap']);
  const used = countOf(value['used']);
  const left = countOf(value['left']);

  if (cap === null || used === null || left === null) return undefined;
  if (!withReset) return { cap, used, left };

  const resetsAt = value['resetsAt'];

  return typeof resetsAt === 'string' && resetsAt !== '' ? { cap, used, left, resetsAt } : undefined;
}

/**
 * The notice block, or null when what arrived is not one.
 *
 * Required rather than tolerated as absent: a missing block read as "no notice" would quietly stop telling a
 * creator their credits are running low, which is the one thing this feature exists to do.
 */
function decodeLowCredit(value: unknown): AccountAiLowCredit | null {
  if (!isRecord(value)) return null;

  const isNoticed = value['isNoticed'];
  const isDismissed = value['isDismissed'];
  const noticeEnabled = value['noticeEnabled'];
  const spendable = amountOf(value['spendable']);

  if (
    typeof isNoticed !== 'boolean' ||
    typeof isDismissed !== 'boolean' ||
    typeof noticeEnabled !== 'boolean' ||
    spendable === null
  ) {
    return null;
  }

  return { isNoticed, isDismissed, noticeEnabled, spendable };
}

/**
 * The plan, null when the host names none, or undefined when what arrived is not one. Absent reads as null: a
 * host with no catalogue may leave the field out rather than send it empty.
 */
function decodePlan(value: unknown): AccountAiUsagePlan | null | undefined {
  if (value === null || value === undefined) return null;
  if (!isRecord(value)) return undefined;

  const key = value['key'];
  const name = value['name'];
  const allowanceRenews = value['allowanceRenews'];

  if (typeof key !== 'string' || typeof name !== 'string' || typeof allowanceRenews !== 'boolean') return undefined;

  return { key, name, allowanceRenews };
}

function decodePeriod(value: unknown): AccountAiUsagePeriod | null {
  if (!isRecord(value)) return null;

  const unit = decodeEnum<AiQuotaUnit>(AI_QUOTA_UNIT_VALUES, value['unit']);
  const periodLength = decodeEnum<AiQuotaPeriodLength>(AI_QUOTA_PERIOD_LENGTH_VALUES, value['periodLength']);
  const timeZoneId = value['timeZoneId'];
  const startsAt = value['startsAt'];
  const resetsAt = value['resetsAt'];

  const allowance = amountOf(value['allowance']);
  const carriedOver = amountOf(value['carriedOver']);
  const consumed = amountOf(value['consumed']);
  const reserved = amountOf(value['reserved']);
  const remaining = amountOf(value['remaining']);

  if (
    unit === null ||
    periodLength === null ||
    typeof timeZoneId !== 'string' ||
    typeof startsAt !== 'string' ||
    typeof resetsAt !== 'string' ||
    allowance === null ||
    carriedOver === null ||
    consumed === null ||
    reserved === null ||
    remaining === null
  ) {
    return null;
  }

  return { unit, allowance, carriedOver, consumed, reserved, remaining, periodLength, timeZoneId, startsAt, resetsAt };
}

function decodeTaskTotal(value: unknown): AccountAiUsageTaskTotal | null {
  if (!isRecord(value)) return null;

  const taskType = decodeEnum<AiTaskType>(AI_TASK_TYPE_VALUES, value['taskType']);
  const amount = amountOf(value['amount']);
  const requests = countOf(value['requests']);

  if (amount === null || requests === null) return null;

  // A capability this build has never heard of folds into `Unspecified` rather than being dropped. Adding an
  // AiTaskType is an additive, non-breaking server change, and a dropped row would silently stop the
  // breakdown summing to what the period says was consumed — the invariant the workspace rows below spell
  // out. `Unspecified` already reads as "Other AI work" on screen, so the spend stays visible and honest.
  return { taskType: taskType ?? 'Unspecified', amount, requests };
}

function decodeWorkspaceTotal(value: unknown): AccountAiUsageWorkspaceTotal | null {
  if (!isRecord(value)) return null;

  const workspaceId = value['workspaceId'];
  const workspaceName = value['workspaceName'];
  const amount = amountOf(value['amount']);
  const requests = countOf(value['requests']);

  if (!isStringOrNull(workspaceId) || !isStringOrNull(workspaceName) || amount === null || requests === null) {
    return null;
  }

  return { workspaceId, workspaceName, amount, requests };
}

/**
 * A decimal amount, which the server may serialize as a number or as a string.
 *
 * Both are in the published contract — the snapshot types every amount as `["number","string"]` with a
 * numeric pattern — because a decimal that went through a double would round. Nothing here is arithmetic a
 * creator's balance depends on; the server already did that, and these are figures to display.
 */
function amountOf(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string') return null;

  const parsed = Number(value);
  return value.trim().length > 0 && Number.isFinite(parsed) ? parsed : null;
}

/**
 * A whole count. The contract publishes `requests` as an integer (or its string form), so a decimal here
 * would mean the server sent something it says it cannot.
 */
function countOf(value: unknown): number | null {
  const parsed = amountOf(value);

  return parsed !== null && Number.isInteger(parsed) ? parsed : null;
}

/** Drops any entry that fails to decode, the same way the memberships read does. */
function decodeList<T>(value: unknown, decode: (entry: unknown) => T | null): readonly T[] | null {
  // Both breakdowns are required by the contract. A missing one is a server that changed underneath us, and
  // rendering it as a healthy account with no usage would be a confident wrong answer.
  if (!Array.isArray(value)) return null;

  const decoded: T[] = [];
  for (const entry of value) {
    const item = decode(entry);
    if (item !== null) decoded.push(item);
  }

  return decoded;
}
