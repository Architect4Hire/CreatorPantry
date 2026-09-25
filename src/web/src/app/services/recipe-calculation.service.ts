import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  ConvertTemperatureRequest,
  ConvertUnitsRequest,
  RecipeTemperatureConversionResult,
  RecipeUnitConversionResult,
  decodeRecipeTemperatureConversionResult,
  decodeRecipeUnitConversionResult,
  encodeConvertTemperatureRequest,
  encodeConvertUnitsRequest,
} from '../models/recipe-conversion.models';
import {
  NormalizeDisplayRequest,
  RecipeQuantityDisplayResult,
  decodeRecipeQuantityDisplayResult,
  encodeNormalizeDisplayRequest,
} from '../models/recipe-display.models';
import {
  RecipeScalingResult,
  ScaleRecipeRequest,
  decodeRecipeScalingResult,
  encodeScaleRecipeRequest,
} from '../models/recipe-scaling.models';
import {
  RecalculateYieldRequest,
  RecipeYieldReconciliationResult,
  decodeRecipeYieldReconciliationResult,
  encodeRecalculateYieldRequest,
} from '../models/recipe-yield.models';

export type ScaleRecipeOutcome =
  | { readonly status: 'scaled'; readonly result: RecipeScalingResult }
  /** The factor or target yield is one the server will not resolve; `fieldErrors` names which. */
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** The recipe is readable and has no version with that number — the recipe moved on under the preview. */
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable (tenancy.md). */
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/**
 * A unit conversion's outcome.
 *
 * `invalid_request` is not only a malformed request: it is also every pair of units that **cannot** be
 * bridged — different dimensions, a Mass/Volume pair with no approved density, or a temperature unit, which
 * is a different operation entirely. Each arrives naming `toUnitId` with the server's own sentence, and none
 * of them is ever a partial or approximate success (ING-004).
 */
export type ConvertUnitsOutcome =
  | { readonly status: 'converted'; readonly result: RecipeUnitConversionResult }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

export type ConvertTemperatureOutcome =
  | { readonly status: 'converted'; readonly result: RecipeTemperatureConversionResult }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/**
 * A yield reconciliation's outcome.
 *
 * Note what is **not** a refusal: too few values to compute anything is a successful `reconciled` carrying
 * `InsufficientInput`, and three values that contradict each other is a successful `reconciled` carrying
 * `Contradictory`. Both are conclusions about the recipe's own numbers, and ING-005 asks for them to be shown
 * as such rather than as a failed request. `invalid_request` is only a value that is zero or negative.
 */
export type RecalculateYieldOutcome =
  | { readonly status: 'reconciled'; readonly result: RecipeYieldReconciliationResult }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

export type NormalizeDisplayOutcome =
  | { readonly status: 'rendered'; readonly result: RecipeQuantityDisplayResult }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body, which is what distinguishes one 404 from another. */
function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

/** `ValidationProblemDetails.errors`, keyed by camelCase request field name (`OperationError.FieldErrors`). */
function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const errors = body?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }
  return result;
}

/** RecipeErrorCodes.VersionNotFound — distinguishes a missing version from an unreadable recipe on a 404. */
const VERSION_NOT_FOUND_CODE = 'recipes.version.not_found';

/**
 * The refusals every calculation route shares. Each one names an exact source version and validates its own
 * inputs, so they fail in exactly the same four ways and answer them identically.
 */
type CalculationRefusal =
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'version_not_found'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'not_found' }
  /** The session is gone, or this member may not read the recipe. Retrying will not help. */
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

function refusalFor(error: unknown): CalculationRefusal {
  const status = statusCodeOf(error);
  if (status === 400) return { status: 'invalid_request', fieldErrors: decodeFieldErrors(error) };

  // Kept apart from `unavailable`, which every panel reports as a connection problem. An expired session is
  // not a network fault, and telling a creator to check their connection sends them to fix the wrong thing.
  if (status === 401 || status === 403) return { status: 'unauthorized' };

  // Both are 404, and the stable code is what tells them apart: a version this recipe does not have is
  // something the creator can act on by reloading, while a recipe they cannot see is not explained further —
  // saying more would disclose that it exists (tenancy.md).
  if (status === 404) {
    return problemCodeOf(error) === VERSION_NOT_FOUND_CODE
      ? { status: 'version_not_found', fieldErrors: decodeFieldErrors(error) }
      : { status: 'not_found' };
  }

  return { status: 'unavailable' };
}

function calculationUrl(
  apiBase: ApiBaseService,
  workspaceSlug: string,
  recipeId: string,
  operation: string,
): string | null {
  return apiBase.url(
    `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipes/${encodeURIComponent(recipeId)}/calculations/${operation}`,
  );
}

/**
 * The typed client for the recipe `calculations/*` family. Every route behind it is **read-only**: a
 * calculation returns a proposal with its own provenance and never writes anything, so nothing here takes an
 * idempotency key or a concurrency token, and no response replaces canonical recipe content until an explicit
 * apply seam asks it to.
 *
 * Separate from `RecipeService`, which owns the recipe's own CRUD and version commands, because these share a
 * shape that CRUD does not: each names an exact source version and answers with inputs, result and warnings.
 *
 * No component may inject `HttpClient` directly for these calls; this service is the only path
 * (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class RecipeCalculationService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Computes a scaling preview for one explicit recipe version (ING-003).
   *
   * The source version is required rather than defaulted, so the answer is never ambiguous about what was
   * scaled: a preview shown beside a recipe that has since moved on is a preview of the older content, and
   * the echoed `sourceVersionNumber` is what lets a caller notice.
   */
  async scaleRecipe(
    workspaceSlug: string,
    recipeId: string,
    request: ScaleRecipeRequest,
  ): Promise<ScaleRecipeOutcome> {
    const url = calculationUrl(this.apiBase, workspaceSlug, recipeId, 'scale');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeScaleRecipeRequest(request), { withCredentials: true }),
      );
      const result = decodeRecipeScalingResult(raw);
      return result ? { status: 'scaled', result } : { status: 'unavailable' };
    } catch (error) {
      return refusalFor(error);
    }
  }

  /**
   * Converts one quantity between two units, in the context of one explicit recipe version (ING-004).
   *
   * **Same-dimension conversion only.** A Mass/Volume pair needs an approved ingredient density, which
   * nothing resolves yet, and a temperature unit is a different operation — see
   * {@link convertTemperature}. Both are refused rather than approximated, and arrive as an
   * `invalid_request` naming `toUnitId`: an unsupported conversion is never a partial success.
   *
   * The quantity converted is the caller's own. This route reads no ingredient line from the named version —
   * the version is there so the preview carries the same provenance and workspace isolation every
   * calculation route does.
   */
  async convertUnits(
    workspaceSlug: string,
    recipeId: string,
    request: ConvertUnitsRequest,
  ): Promise<ConvertUnitsOutcome> {
    const url = calculationUrl(this.apiBase, workspaceSlug, recipeId, 'convert-units');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeConvertUnitsRequest(request), { withCredentials: true }),
      );
      const result = decodeRecipeUnitConversionResult(raw);
      return result ? { status: 'converted', result } : { status: 'unavailable' };
    } catch (error) {
      return refusalFor(error);
    }
  }

  /**
   * Converts one already-structured temperature between Celsius and Fahrenheit (CALC-003).
   *
   * `value` is a number the caller already knows is a temperature — a step's recorded `temperatureValue`,
   * never its prose. recipes.md forbids inferring a temperature from vague heat language, and there is no
   * field on this request that could carry one.
   *
   * `ovenModeContext` and `safetyNote` are carried through the calculation untouched and echoed back
   * verbatim. Neither is read, parsed, or used to adjust the number, and echoing a safety note back is not
   * the system confirming anything about it.
   */
  async convertTemperature(
    workspaceSlug: string,
    recipeId: string,
    request: ConvertTemperatureRequest,
  ): Promise<ConvertTemperatureOutcome> {
    const url = calculationUrl(this.apiBase, workspaceSlug, recipeId, 'convert-temperature');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeConvertTemperatureRequest(request), { withCredentials: true }),
      );
      const result = decodeRecipeTemperatureConversionResult(raw);
      return result ? { status: 'converted', result } : { status: 'unavailable' };
    } catch (error) {
      return refusalFor(error);
    }
  }

  /**
   * Reconciles a recipe's batch yield, serving count, serving size and pan capacity (ING-005).
   *
   * Every value is optional, and **nothing is taken from the recipe's own stored numbers** — what the caller
   * sends is everything the calculation reasons about. Too few values to compute anything, and three values
   * that contradict each other, are both successful answers carrying a status; neither invents a serving
   * definition and neither decides which of the three is wrong (recipes.md).
   *
   * The dimension the amounts are read in comes from the named version's own yield unit, resolved
   * server-side. It is never sent, and a recipe with no yield unit is read as a plain count — which is also
   * what makes a pan comparison impossible, since that needs a volume.
   */
  async recalculateYield(
    workspaceSlug: string,
    recipeId: string,
    request: RecalculateYieldRequest,
  ): Promise<RecalculateYieldOutcome> {
    const url = calculationUrl(this.apiBase, workspaceSlug, recipeId, 'recalculate-yield');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeRecalculateYieldRequest(request), { withCredentials: true }),
      );
      const result = decodeRecipeYieldReconciliationResult(raw);
      return result ? { status: 'reconciled', result } : { status: 'unavailable' };
    } catch (error) {
      return refusalFor(error);
    }
  }

  /**
   * Renders one quantity, or one range, as readable text (ING-006).
   *
   * **Presentation only.** The value sent stays canonical; the text that comes back is how it reads and never
   * what it is, and nothing here is persisted or fed back into a recipe quantity.
   */
  async normalizeDisplay(
    workspaceSlug: string,
    recipeId: string,
    request: NormalizeDisplayRequest,
  ): Promise<NormalizeDisplayOutcome> {
    const url = calculationUrl(this.apiBase, workspaceSlug, recipeId, 'normalize-display');
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeNormalizeDisplayRequest(request), { withCredentials: true }),
      );
      const result = decodeRecipeQuantityDisplayResult(raw);
      return result ? { status: 'rendered', result } : { status: 'unavailable' };
    } catch (error) {
      return refusalFor(error);
    }
  }
}
