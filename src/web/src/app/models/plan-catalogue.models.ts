// Wire models for GET /api/v1/plans. Mirrors
// CreatorPantry.Domain/Modules/Billing/Managers/PlanCatalogueServiceModels.cs exactly.
//
// The catalogue is what a deployment has chosen to publish, and a deployment that publishes nothing answers
// with empty lists rather than an error. So an empty catalogue is a state to render as "no pricing section",
// never a failure to report.
//
// No figure in here has a counterpart in this bundle: prices, credits and caps are the server's, read at
// runtime, so a price can change without a release and the browser can never disagree with the catalogue.

import {
  GENERATED_IMAGE_QUALITY_CLASS_VALUES,
  GeneratedImageQualityClass,
} from './generated-image.models';
import { decodeEnum, isRecord } from './recipe.models';

/** One plan on offer. Money is a number in the catalogue's currency, formatted for the reader at the edge. */
export interface Plan {
  readonly key: string;
  readonly name: string;
  readonly monthlyPrice: number;
  /** What a month works out at when the year is paid up front. */
  readonly annualMonthlyPrice: number;
  /** What the year actually costs. */
  readonly annualPrice: number;
  /** What paying yearly saves against twelve monthly payments. The server's figure, never worked out here. */
  readonly annualSaving: number;
  readonly credits: number;
  /** What those credits buy, in the creator's terms. An illustration, not an entitlement. */
  readonly exampleMix: string;
  readonly dailyImageCap: number;
  readonly qualityClasses: readonly string[];
  readonly recommended: boolean;
  /** Days a trial lasts; null on a paid plan. */
  readonly trialDays: number | null;
  /** Pictures the whole trial may make, beside its daily cap; null on a paid plan. The trial's gate (B-52). */
  readonly trialImageCap: number | null;
  /**
   * How many pictures these credits buy at the catalogue's default quality (AP.4.5) — the figure that decides
   * which plan a creator needs, since pictures are what credits mostly go on.
   *
   * The server's, because the browser may not divide credits by a rate. Already bounded by a trial's lifetime
   * cap. Null when the catalogue publishes no default quality or cannot price it.
   */
  readonly picturesAtDefaultQuality: number | null;
}

/**
 * A prepaid credit pack. It has no expiry: a pack is credits the creator has bought, so they keep until they
 * are spent (owner, 2026-10-24).
 */
export interface CreditPack {
  readonly key: string;
  readonly credits: number;
  readonly price: number;
}

/**
 * What one action of each kind typically costs, in credits (LP.7). Typical, and said so wherever shown: a run
 * settles against the provider's own usage, and the allowance is enforced server-side, never by these.
 */
export interface CreditRates {
  readonly textDraft: number;
  readonly longFormSection: number;
}

/**
 * What one picture of one quality costs, in credits (PRICE-006, PRICE-013, AP.4.5).
 *
 * Not typical, unlike the rates above: this is the figure admission charges for that class, read from the same
 * table, so no line that states it says "about". Mirrors `PictureClassRateServiceModel`.
 */
export interface PictureClassRate {
  /** Mirrors `GeneratedImageQualityClass`. A class this bundle does not know is dropped from the list. */
  readonly qualityClass: GeneratedImageQualityClass;
  readonly perPicture: number;
}

export interface PlanCatalogue {
  readonly currency: string;
  readonly plans: readonly Plan[];
  readonly packs: readonly CreditPack[];
  /**
   * Null when the catalogue states no rates — a deployment that publishes none, or a server older than the
   * menu that reads them. The credit menu then does not render, and the plans above it are untouched: one
   * section's missing data is not a reason to take the pricing off the page.
   */
  readonly rates: CreditRates | null;
  /**
   * What a picture of each quality costs, cheapest first (AP.4.5). Empty when the catalogue publishes none — a
   * deployment with no plans, or one whose plans reach no class this bundle knows. The menu then states no
   * picture line and the estimator offers no quality, rather than pricing every picture as one kind.
   */
  readonly pictureClasses: readonly PictureClassRate[];
  /**
   * The quality a picture is made at when a creator chooses none (AP.4.5). Null when the catalogue publishes
   * none, or names one it does not price — the estimator then starts from the cheapest it was given, which is
   * the only other figure it can honestly use.
   */
  readonly defaultPictureClass: GeneratedImageQualityClass | null;
}

function numberAt(value: Record<string, unknown>, key: string): number | null {
  const raw = value[key];
  return typeof raw === 'number' && Number.isFinite(raw) ? raw : null;
}

/**
 * An optional figure: absent reads as null, and so does an explicit null. A value that is *there* but is not a
 * number is `undefined` — a refusal rather than a null, because "14" quietly becoming "no trial" would have the
 * page tell a visitor the opposite of what the catalogue says.
 */
function optionalNumberAt(value: Record<string, unknown>, key: string): number | null | undefined {
  const raw = value[key];
  if (raw === null || raw === undefined) return null;
  return typeof raw === 'number' && Number.isFinite(raw) ? raw : undefined;
}

function stringAt(value: Record<string, unknown>, key: string): string | null {
  const raw = value[key];
  return typeof raw === 'string' ? raw : null;
}

/** Decodes one plan; returns null unless every field the page shows is present and of the right type. */
export function decodePlan(value: unknown): Plan | null {
  if (!isRecord(value)) return null;

  const key = stringAt(value, 'key');
  const name = stringAt(value, 'name');
  const exampleMix = stringAt(value, 'exampleMix');
  const monthlyPrice = numberAt(value, 'monthlyPrice');
  const annualMonthlyPrice = numberAt(value, 'annualMonthlyPrice');
  const annualPrice = numberAt(value, 'annualPrice');
  const annualSaving = numberAt(value, 'annualSaving');
  const credits = numberAt(value, 'credits');
  const dailyImageCap = numberAt(value, 'dailyImageCap');
  const qualityClasses = value['qualityClasses'];
  const trialDays = optionalNumberAt(value, 'trialDays');
  const trialImageCap = optionalNumberAt(value, 'trialImageCap');
  const picturesAtDefaultQuality = optionalNumberAt(value, 'picturesAtDefaultQuality');

  if (
    key === null ||
    name === null ||
    exampleMix === null ||
    monthlyPrice === null ||
    annualMonthlyPrice === null ||
    annualPrice === null ||
    annualSaving === null ||
    credits === null ||
    dailyImageCap === null ||
    trialDays === undefined ||
    trialImageCap === undefined ||
    picturesAtDefaultQuality === undefined ||
    !Array.isArray(qualityClasses)
  ) {
    return null;
  }

  return {
    key,
    name,
    monthlyPrice,
    annualMonthlyPrice,
    annualPrice,
    annualSaving,
    credits,
    exampleMix,
    dailyImageCap,
    qualityClasses: qualityClasses.filter((entry): entry is string => typeof entry === 'string'),
    recommended: value['recommended'] === true,
    trialDays,
    trialImageCap,
    picturesAtDefaultQuality,
  };
}

export function decodeCreditPack(value: unknown): CreditPack | null {
  if (!isRecord(value)) return null;

  const key = stringAt(value, 'key');
  const credits = numberAt(value, 'credits');
  const price = numberAt(value, 'price');

  return key === null || credits === null || price === null ? null : { key, credits, price };
}

/**
 * Decodes the catalogue. A plan that does not decode is dropped rather than failing the lot: a page that
 * showed nothing because one plan grew a field would be worse than one that shows the plans it understood.
 * A body that is not a catalogue at all returns null, which the page treats as "no pricing to show".
 */
/** Decodes the rates; returns null unless every one of them is a number, since the menu states them all. */
export function decodeCreditRates(value: unknown): CreditRates | null {
  if (!isRecord(value)) return null;

  const rates = {
    textDraft: numberAt(value, 'textDraft'),
    longFormSection: numberAt(value, 'longFormSection'),
  };

  return Object.values(rates).some((rate) => rate === null) ? null : (rates as CreditRates);
}

export function decodePlanCatalogue(value: unknown): PlanCatalogue | null {
  if (!isRecord(value)) return null;

  const currency = stringAt(value, 'currency');
  const plans = value['plans'];
  const packs = value['packs'];
  if (currency === null || !Array.isArray(plans) || !Array.isArray(packs)) return null;

  const pictures = decodePictureClassRates(value['pictureClasses']);
  const fallback = decodeEnum<GeneratedImageQualityClass>(
    GENERATED_IMAGE_QUALITY_CLASS_VALUES,
    value['defaultPictureClass'],
  );

  return {
    currency,
    plans: plans.map(decodePlan).filter((plan): plan is Plan => plan !== null),
    packs: packs.map(decodeCreditPack).filter((pack): pack is CreditPack => pack !== null),
    rates: decodeCreditRates(value['rates']),
    pictureClasses: pictures,
    // Only a default the list actually prices: one it does not is no quality to start an estimate from.
    defaultPictureClass: pictures.some((row) => row.qualityClass === fallback) ? fallback : null,
  };
}

/**
 * Decodes the per-picture prices, cheapest first as the server sent them.
 *
 * A row naming a class this bundle does not know is dropped rather than refusing the list, exactly as the
 * studio's own price list drops one: a class added on the server is then simply not advertised here, which is
 * safe, where refusing would take the whole menu off the page. A price that is there but not a number drops its
 * row too — a quality shown with no figure would read as free.
 */
export function decodePictureClassRates(value: unknown): readonly PictureClassRate[] {
  if (!Array.isArray(value)) return [];

  const rates: PictureClassRate[] = [];

  for (const row of value as unknown[]) {
    if (!isRecord(row)) continue;

    const qualityClass = decodeEnum<GeneratedImageQualityClass>(
      GENERATED_IMAGE_QUALITY_CLASS_VALUES,
      row['qualityClass'],
    );
    const perPicture = numberAt(row, 'perPicture');

    if (qualityClass !== null && perPicture !== null) {
      rates.push({ qualityClass, perPicture });
    }
  }

  return rates;
}
