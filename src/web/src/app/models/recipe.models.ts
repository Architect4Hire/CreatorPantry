// Wire models for the Phase 5 recipe create/detail/update endpoints
// (POST|GET|PATCH /api/v1/workspaces/{workspaceSlug}/recipes[/{recipeId}]). Mirrors
// CreatorPantry.Domain/Modules/Recipes/Managers/{CreateRecipeViewModel,UpdateRecipeViewModel,
// RecipeCreationModels,RecipeDetailServiceModel}.cs exactly — do not add fields the backend
// doesn't send, and keep this file in sync when those ViewModels/ServiceModels change.
//
// Every recipe enum is serialized with a global JsonStringEnumConverter (registered in
// CreatorPantry.ApiService/Program.cs and confirmed against the committed OpenAPI snapshot) — they
// are PascalCase member names on the wire, never integers. Decoding is a membership check against
// the literal union below, not a number lookup.

export type RecipeStatus = 'Draft' | 'Ready' | 'Archived';
export type SettableRecipeStatus = 'Draft' | 'Ready' | 'Archived';
export type RecipeVersionSource = 'CreatorEdit' | 'AiProposalAccepted' | 'Import' | 'Restore' | 'Duplicate';
export type RecipeVersionReadiness = 'Draft' | 'Ready';
export type IngredientMatchStatus = 'NotAttempted' | 'Matched' | 'NoMatch' | 'Ambiguous';
export type IngredientScaling = 'Proportional' | 'Fixed' | 'ReviewRequired';
export type RecipeAssetRole = 'Hero' | 'Gallery' | 'Process';

const RECIPE_STATUS_VALUES: ReadonlySet<string> = new Set<RecipeStatus>(['Draft', 'Ready', 'Archived']);
export const RECIPE_VERSION_SOURCE_VALUES: ReadonlySet<string> = new Set<RecipeVersionSource>([
  'CreatorEdit',
  'AiProposalAccepted',
  'Import',
  'Restore',
  'Duplicate',
]);
export const RECIPE_VERSION_READINESS_VALUES: ReadonlySet<string> = new Set<RecipeVersionReadiness>(['Draft', 'Ready']);
const INGREDIENT_MATCH_STATUS_VALUES: ReadonlySet<string> = new Set<IngredientMatchStatus>([
  'NotAttempted',
  'Matched',
  'NoMatch',
  'Ambiguous',
]);
const INGREDIENT_SCALING_VALUES: ReadonlySet<string> = new Set<IngredientScaling>(['Proportional', 'Fixed', 'ReviewRequired']);
const RECIPE_ASSET_ROLE_VALUES: ReadonlySet<string> = new Set<RecipeAssetRole>(['Hero', 'Gallery', 'Process']);

export function decodeEnum<T extends string>(values: ReadonlySet<string>, value: unknown): T | null {
  return typeof value === 'string' && values.has(value) ? (value as T) : null;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

export function isStringOrNull(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

export function isNumberOrNull(value: unknown): value is number | null {
  return value === null || typeof value === 'number';
}

// ---------------------------------------------------------------------------
// PatchField<T> — mirrors CreatorPantry.Domain.Managers.Patching.PatchField<T> for the PATCH request
// body. "Absent" fields must be omitted from the JSON entirely (JSON Merge Patch semantics: omitted =
// leave unchanged); "submitted" fields are sent with their value, including an explicit null to clear
// a clearable field. Never invent a third state on the wire.
// ---------------------------------------------------------------------------

export type PatchField<T> = { readonly submitted: false } | { readonly submitted: true; readonly value: T };

export function absent<T>(): PatchField<T> {
  return { submitted: false };
}

export function submitted<T>(value: T): PatchField<T> {
  return { submitted: true, value };
}

// ---------------------------------------------------------------------------
// Request models
// ---------------------------------------------------------------------------

/**
 * Mirrors RecipeInstructionGroupInputViewModel/RecipeInstructionStepInputViewModel — shared by create and
 * update. `id` names an existing group/step to edit in place; omit it (or send `null`) for a new one. On
 * create, sending an `id` at all is refused server-side, since nothing exists yet to name.
 */
export interface InstructionGroupInput {
  readonly id?: string | null;
  readonly title?: string | null;
  readonly steps?: readonly InstructionStepInput[] | null;
}

export interface InstructionStepInput {
  readonly id?: string | null;
  readonly text: string;
  readonly techniqueId?: string | null;
  readonly durationMinutes?: number | null;
  readonly temperatureValue?: number | null;
  readonly temperatureUnitId?: string | null;
  readonly note?: string | null;
}

/**
 * Mirrors RecipeIngredientGroupInputViewModel/RecipeIngredientInputViewModel — shared by create and update,
 * exactly like InstructionGroupInput/InstructionStepInput above. `id` names an existing group/line to edit in
 * place; omit it (or send `null`) for a new one. On create, sending an `id` at all is refused server-side.
 */
export interface IngredientGroupInput {
  readonly id?: string | null;
  readonly title?: string | null;
  readonly ingredients?: readonly IngredientInput[] | null;
}

export interface IngredientInput {
  readonly id?: string | null;
  /** The creator's own wording for the whole line, exactly as entered. Never rewritten by a matched `ingredientId`. */
  readonly displayText: string;
  readonly quantity?: number | null;
  readonly quantityUpper?: number | null;
  readonly measurementUnitId?: string | null;
  readonly ingredientId?: string | null;
  readonly preparationNote?: string | null;
  readonly isOptional?: boolean | null;
  readonly scalingBehavior?: IngredientScaling | null;
}

/** Mirrors CreateRecipeViewModel, whose `Title` is itself `string?` — required is a server-side validation rule, not a wire-level constraint. */
export interface CreateRecipeRequest {
  readonly title?: string | null;
  readonly description?: string | null;
  readonly headnote?: string | null;
  readonly notes?: string | null;
  readonly storageNotes?: string | null;
  readonly attributionText?: string | null;
  readonly sourceUrl?: string | null;
  readonly cuisineId?: string | null;
  readonly courseId?: string | null;
  readonly primaryTechniqueId?: string | null;
  readonly prepTimeMinutes?: number | null;
  readonly cookTimeMinutes?: number | null;
  readonly restTimeMinutes?: number | null;
  readonly totalTimeMinutes?: number | null;
  readonly yieldText?: string | null;
  readonly yieldQuantity?: number | null;
  readonly yieldUnitId?: string | null;
  readonly tags?: readonly string[] | null;
  /** Omit to let the server default to Draft. Archived is rejected server-side on create. */
  readonly status?: SettableRecipeStatus;
  readonly instructions?: readonly InstructionGroupInput[] | null;
  readonly ingredientGroups?: readonly IngredientGroupInput[] | null;
}

/** Builds the JSON body for POST .../recipes. `status`, when present, is already the wire's PascalCase name. */
export function encodeCreateRecipeRequest(request: CreateRecipeRequest): Record<string, unknown> {
  return { ...request };
}

/**
 * Mirrors DuplicateRecipeViewModel — the body of POST .../recipes/{recipeId}/duplicate.
 *
 * A title and, optionally, which version to copy. There is deliberately nothing else: no concurrency token,
 * because nothing is being overwritten, and no content, because everything but the title comes from the
 * source version. A client cannot smuggle a change into a copy.
 */
export interface DuplicateRecipeRequest {
  /** The copy's title. Required by the server, which will not invent one. */
  readonly title: string;

  /** A version number as the history lists it, or null to copy the recipe as it currently stands. */
  readonly sourceVersionNumber: number | null;
}

/** Builds the JSON body for POST .../recipes/{recipeId}/duplicate, trimming the title and omitting an absent version. */
export function encodeDuplicateRecipeRequest(request: DuplicateRecipeRequest): Record<string, unknown> {
  const title = request.title.trim();

  return request.sourceVersionNumber === null ? { title } : { title, sourceVersionNumber: request.sourceVersionNumber };
}

/**
 * Mirrors RecipeLifecycleViewModel — the body of both POST .../recipes/{recipeId}/archive and
 * .../unarchive.
 *
 * One type for both, as it is server-side: each carries the same single field, and the route says which way
 * the recipe is going, so a body that also said it would be a second source of truth. There is no reason
 * field — these commands write no version, and their trace is an audit entry rather than creator prose.
 */
export interface RecipeLifecycleRequest {
  /** The `concurrencyToken` from the recipe as the creator last saw it. Required. */
  readonly expectedConcurrencyToken: string;
}

/** Mirrors UpdateRecipeViewModel. Every content field is a PatchField so "unchanged" and "cleared" stay distinguishable. */
export interface UpdateRecipeRequest {
  readonly expectedConcurrencyToken: string;
  readonly reason?: string;
  readonly title?: PatchField<string>;
  readonly description?: PatchField<string | null>;
  readonly headnote?: PatchField<string | null>;
  readonly notes?: PatchField<string | null>;
  readonly storageNotes?: PatchField<string | null>;
  readonly attributionText?: PatchField<string | null>;
  readonly sourceUrl?: PatchField<string | null>;
  readonly cuisineId?: PatchField<string | null>;
  readonly courseId?: PatchField<string | null>;
  readonly primaryTechniqueId?: PatchField<string | null>;
  readonly prepTimeMinutes?: PatchField<number | null>;
  readonly cookTimeMinutes?: PatchField<number | null>;
  readonly restTimeMinutes?: PatchField<number | null>;
  readonly totalTimeMinutes?: PatchField<number | null>;
  readonly yieldText?: PatchField<string | null>;
  readonly yieldQuantity?: PatchField<number | null>;
  readonly yieldUnitId?: PatchField<string | null>;
  readonly tags?: PatchField<readonly string[] | null>;
  readonly status?: PatchField<SettableRecipeStatus>;
  /**
   * The recipe's complete method. Submitting replaces: a group/step named by `id` is updated in place,
   * one without an `id` is new, and one the recipe currently has but this list does not name is removed.
   */
  readonly instructions?: PatchField<readonly InstructionGroupInput[] | null>;
  /**
   * The recipe's complete ingredient list. Submitting replaces: a group/line named by `id` is updated in
   * place, one without an `id` is new, and one the recipe currently has but this list does not name is
   * removed.
   */
  readonly ingredientGroups?: PatchField<readonly IngredientGroupInput[] | null>;
}

const PATCH_FIELD_KEYS = [
  'title',
  'description',
  'headnote',
  'notes',
  'storageNotes',
  'attributionText',
  'sourceUrl',
  'cuisineId',
  'courseId',
  'primaryTechniqueId',
  'prepTimeMinutes',
  'cookTimeMinutes',
  'restTimeMinutes',
  'totalTimeMinutes',
  'yieldText',
  'yieldQuantity',
  'yieldUnitId',
  'tags',
  'status',
  'instructions',
  'ingredientGroups',
] as const satisfies readonly (keyof UpdateRecipeRequest)[];

/**
 * Builds the JSON Merge Patch body for PATCH .../recipes/{id}: a field left as `undefined` (never
 * called with `submitted(...)`) is omitted entirely so the server leaves it untouched; a field passed
 * as `submitted(value)` is included as-is (a submitted `status` is already the wire's PascalCase name).
 */
export function encodeUpdateRecipeRequest(request: UpdateRecipeRequest): Record<string, unknown> {
  const body: Record<string, unknown> = { expectedConcurrencyToken: request.expectedConcurrencyToken };
  if (request.reason !== undefined) body['reason'] = request.reason;

  for (const key of PATCH_FIELD_KEYS) {
    const field = request[key] as PatchField<unknown> | undefined;
    if (!field?.submitted) continue;
    body[key] = field.value;
  }

  return body;
}

// ---------------------------------------------------------------------------
// Response models
// ---------------------------------------------------------------------------

/** Mirrors CreatedRecipeServiceModel — the POST .../recipes response body. */
export interface CreatedRecipe {
  readonly recipeId: string;
  readonly title: string;
  readonly status: RecipeStatus;
  readonly versionId: string;
  readonly versionNumber: number;
  readonly createdAt: string;
}

export function decodeCreatedRecipe(value: unknown): CreatedRecipe | null {
  if (!isRecord(value)) return null;
  const { recipeId, title, status: rawStatus, versionId, versionNumber, createdAt } = value;
  const status = decodeEnum<RecipeStatus>(RECIPE_STATUS_VALUES, rawStatus);

  if (
    typeof recipeId !== 'string' ||
    typeof title !== 'string' ||
    status === null ||
    typeof versionId !== 'string' ||
    typeof versionNumber !== 'number' ||
    typeof createdAt !== 'string'
  ) {
    return null;
  }

  return { recipeId, title, status, versionId, versionNumber, createdAt };
}

/**
 * Mirrors RecipeDuplicateSourceServiceModel. Everything a "copied from …" affordance needs in one object:
 * `recipeId` is navigable — lineage never crosses a workspace, so a recipe named here is always one the
 * caller may read — and `recipeTitle` is that recipe's current title, not a snapshot of it.
 */
export interface RecipeDuplicateSource {
  readonly recipeId: string;
  readonly recipeTitle: string;
  readonly versionId: string;
  readonly versionNumber: number;
}

function decodeRecipeDuplicateSource(value: unknown): RecipeDuplicateSource | null {
  if (!isRecord(value)) return null;

  const { recipeId, recipeTitle, versionId, versionNumber } = value;

  if (
    typeof recipeId !== 'string' ||
    typeof recipeTitle !== 'string' ||
    typeof versionId !== 'string' ||
    typeof versionNumber !== 'number'
  ) {
    return null;
  }

  return { recipeId, recipeTitle, versionId, versionNumber };
}

/** Mirrors RecipeVersionSummaryServiceModel. */
export interface RecipeVersionSummary {
  readonly id: string;
  readonly versionNumber: number;
  readonly source: RecipeVersionSource;
  readonly readiness: RecipeVersionReadiness;
  readonly reason: string | null;
  readonly createdAt: string;
}

function decodeRecipeVersionSummary(value: unknown): RecipeVersionSummary | null {
  if (!isRecord(value)) return null;
  const { id, versionNumber, source: rawSource, readiness: rawReadiness, reason, createdAt } = value;
  const source = decodeEnum<RecipeVersionSource>(RECIPE_VERSION_SOURCE_VALUES, rawSource);
  const readiness = decodeEnum<RecipeVersionReadiness>(RECIPE_VERSION_READINESS_VALUES, rawReadiness);

  if (
    typeof id !== 'string' ||
    typeof versionNumber !== 'number' ||
    source === null ||
    readiness === null ||
    !isStringOrNull(reason) ||
    typeof createdAt !== 'string'
  ) {
    return null;
  }

  return { id, versionNumber, source, readiness, reason, createdAt };
}

/** Mirrors RecipeIngredientServiceModel. */
export interface RecipeIngredient {
  readonly id: string;
  readonly sortOrder: number;
  readonly displayText: string;
  readonly ingredientNameText: string | null;
  readonly quantity: number | null;
  readonly quantityUpper: number | null;
  readonly measurementUnitId: string | null;
  readonly ingredientId: string | null;
  readonly matchStatus: IngredientMatchStatus;
  readonly preparationNote: string | null;
  readonly isOptional: boolean;
  readonly scalingBehavior: IngredientScaling;
}

function decodeRecipeIngredient(value: unknown): RecipeIngredient | null {
  if (!isRecord(value)) return null;
  const {
    id,
    sortOrder,
    displayText,
    ingredientNameText,
    quantity,
    quantityUpper,
    measurementUnitId,
    ingredientId,
    matchStatus: rawMatchStatus,
    preparationNote,
    isOptional,
    scalingBehavior: rawScalingBehavior,
  } = value;
  const matchStatus = decodeEnum<IngredientMatchStatus>(INGREDIENT_MATCH_STATUS_VALUES, rawMatchStatus);
  const scalingBehavior = decodeEnum<IngredientScaling>(INGREDIENT_SCALING_VALUES, rawScalingBehavior);

  if (
    typeof id !== 'string' ||
    typeof sortOrder !== 'number' ||
    typeof displayText !== 'string' ||
    !isStringOrNull(ingredientNameText) ||
    !isNumberOrNull(quantity) ||
    !isNumberOrNull(quantityUpper) ||
    !isStringOrNull(measurementUnitId) ||
    !isStringOrNull(ingredientId) ||
    matchStatus === null ||
    !isStringOrNull(preparationNote) ||
    typeof isOptional !== 'boolean' ||
    scalingBehavior === null
  ) {
    return null;
  }

  return {
    id,
    sortOrder,
    displayText,
    ingredientNameText,
    quantity,
    quantityUpper,
    measurementUnitId,
    ingredientId,
    matchStatus,
    preparationNote,
    isOptional,
    scalingBehavior,
  };
}

/** Mirrors RecipeIngredientGroupServiceModel. */
export interface RecipeIngredientGroup {
  readonly id: string;
  readonly title: string | null;
  readonly sortOrder: number;
  readonly ingredients: readonly RecipeIngredient[];
}

function decodeRecipeIngredientGroup(value: unknown): RecipeIngredientGroup | null {
  if (!isRecord(value)) return null;
  const { id, title, sortOrder, ingredients } = value;
  if (typeof id !== 'string' || !isStringOrNull(title) || typeof sortOrder !== 'number' || !Array.isArray(ingredients)) {
    return null;
  }

  const decodedIngredients = ingredients.map(decodeRecipeIngredient);
  if (decodedIngredients.some((ingredient) => ingredient === null)) return null;

  return { id, title, sortOrder, ingredients: decodedIngredients as RecipeIngredient[] };
}

/** Mirrors RecipeInstructionStepServiceModel. */
export interface RecipeInstructionStep {
  readonly id: string;
  readonly sortOrder: number;
  readonly text: string;
  readonly techniqueId: string | null;
  readonly durationMinutes: number | null;
  readonly temperatureValue: number | null;
  readonly temperatureUnitId: string | null;
  readonly note: string | null;
}

function decodeRecipeInstructionStep(value: unknown): RecipeInstructionStep | null {
  if (!isRecord(value)) return null;
  const { id, sortOrder, text, techniqueId, durationMinutes, temperatureValue, temperatureUnitId, note } = value;

  if (
    typeof id !== 'string' ||
    typeof sortOrder !== 'number' ||
    typeof text !== 'string' ||
    !isStringOrNull(techniqueId) ||
    !isNumberOrNull(durationMinutes) ||
    !isNumberOrNull(temperatureValue) ||
    !isStringOrNull(temperatureUnitId) ||
    !isStringOrNull(note)
  ) {
    return null;
  }

  return { id, sortOrder, text, techniqueId, durationMinutes, temperatureValue, temperatureUnitId, note };
}

/** Mirrors RecipeInstructionGroupServiceModel. */
export interface RecipeInstructionGroup {
  readonly id: string;
  readonly title: string | null;
  readonly sortOrder: number;
  readonly steps: readonly RecipeInstructionStep[];
}

function decodeRecipeInstructionGroup(value: unknown): RecipeInstructionGroup | null {
  if (!isRecord(value)) return null;
  const { id, title, sortOrder, steps } = value;
  if (typeof id !== 'string' || !isStringOrNull(title) || typeof sortOrder !== 'number' || !Array.isArray(steps)) {
    return null;
  }

  const decodedSteps = steps.map(decodeRecipeInstructionStep);
  if (decodedSteps.some((step) => step === null)) return null;

  return { id, title, sortOrder, steps: decodedSteps as RecipeInstructionStep[] };
}

/** Mirrors RecipeEquipmentServiceModel. */
export interface RecipeEquipmentItem {
  readonly id: string;
  readonly sortOrder: number;
  readonly displayText: string;
  readonly equipmentTypeId: string | null;
  readonly isOptional: boolean;
  readonly note: string | null;
}

function decodeRecipeEquipmentItem(value: unknown): RecipeEquipmentItem | null {
  if (!isRecord(value)) return null;
  const { id, sortOrder, displayText, equipmentTypeId, isOptional, note } = value;

  if (
    typeof id !== 'string' ||
    typeof sortOrder !== 'number' ||
    typeof displayText !== 'string' ||
    !isStringOrNull(equipmentTypeId) ||
    typeof isOptional !== 'boolean' ||
    !isStringOrNull(note)
  ) {
    return null;
  }

  return { id, sortOrder, displayText, equipmentTypeId, isOptional, note };
}

/** Mirrors RecipeAssetLinkServiceModel — deliberately id-only; storage URLs are resolved separately. */
export interface RecipeAssetLink {
  readonly id: string;
  readonly sortOrder: number;
  readonly mediaAssetId: string;
  readonly role: RecipeAssetRole;
  readonly caption: string | null;
}

function decodeRecipeAssetLink(value: unknown): RecipeAssetLink | null {
  if (!isRecord(value)) return null;
  const { id, sortOrder, mediaAssetId, role: rawRole, caption } = value;
  const role = decodeEnum<RecipeAssetRole>(RECIPE_ASSET_ROLE_VALUES, rawRole);

  if (
    typeof id !== 'string' ||
    typeof sortOrder !== 'number' ||
    typeof mediaAssetId !== 'string' ||
    role === null ||
    !isStringOrNull(caption)
  ) {
    return null;
  }

  return { id, sortOrder, mediaAssetId, role, caption };
}

/** Mirrors RecipeTagServiceModel. */
export interface RecipeTag {
  readonly workspaceTagId: string;
  readonly name: string;
}

function decodeRecipeTag(value: unknown): RecipeTag | null {
  if (!isRecord(value)) return null;
  const { workspaceTagId, name } = value;
  if (typeof workspaceTagId !== 'string' || typeof name !== 'string') return null;
  return { workspaceTagId, name };
}

/** Mirrors RecipeDetailServiceModel — the GET/PATCH .../recipes/{id} response body. */
export interface RecipeDetail {
  readonly id: string;
  readonly title: string;
  readonly description: string | null;
  readonly headnote: string | null;
  readonly notes: string | null;
  readonly storageNotes: string | null;
  readonly attributionText: string | null;
  readonly sourceUrl: string | null;
  readonly cuisineId: string | null;
  readonly courseId: string | null;
  readonly primaryTechniqueId: string | null;
  readonly prepTimeMinutes: number | null;
  readonly cookTimeMinutes: number | null;
  readonly restTimeMinutes: number | null;
  readonly totalTimeMinutes: number | null;
  readonly yieldText: string | null;
  readonly yieldQuantity: number | null;
  readonly yieldUnitId: string | null;
  readonly status: RecipeStatus;
  /**
   * Where this recipe was copied from, when it was created by duplicating another; null for a recipe
   * someone wrote. Carries the source recipe's id so a "copied from" affordance can link to it, and that
   * recipe's title as it stands now rather than as it was when the copy was made.
   */
  readonly duplicatedFrom: RecipeDuplicateSource | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  /** Opaque row-version token; round-trip verbatim into the next PATCH's `expectedConcurrencyToken`. */
  readonly concurrencyToken: string;
  readonly currentVersion: RecipeVersionSummary | null;
  readonly ingredientGroups: readonly RecipeIngredientGroup[];
  readonly instructionGroups: readonly RecipeInstructionGroup[];
  readonly equipment: readonly RecipeEquipmentItem[];
  readonly assetLinks: readonly RecipeAssetLink[];
  readonly tags: readonly RecipeTag[];
}

export function decodeRecipeDetail(value: unknown): RecipeDetail | null {
  if (!isRecord(value)) return null;
  const {
    id,
    title,
    description,
    headnote,
    notes,
    storageNotes,
    attributionText,
    sourceUrl,
    cuisineId,
    courseId,
    primaryTechniqueId,
    prepTimeMinutes,
    cookTimeMinutes,
    restTimeMinutes,
    totalTimeMinutes,
    yieldText,
    yieldQuantity,
    yieldUnitId,
    status: rawStatus,
    duplicatedFrom: rawDuplicatedFrom,
    createdAt,
    updatedAt,
    concurrencyToken,
    currentVersion: rawCurrentVersion,
    ingredientGroups,
    instructionGroups,
    equipment,
    assetLinks,
    tags,
  } = value;

  const status = decodeEnum<RecipeStatus>(RECIPE_STATUS_VALUES, rawStatus);
  if (
    typeof id !== 'string' ||
    typeof title !== 'string' ||
    !isStringOrNull(description) ||
    !isStringOrNull(headnote) ||
    !isStringOrNull(notes) ||
    !isStringOrNull(storageNotes) ||
    !isStringOrNull(attributionText) ||
    !isStringOrNull(sourceUrl) ||
    !isStringOrNull(cuisineId) ||
    !isStringOrNull(courseId) ||
    !isStringOrNull(primaryTechniqueId) ||
    !isNumberOrNull(prepTimeMinutes) ||
    !isNumberOrNull(cookTimeMinutes) ||
    !isNumberOrNull(restTimeMinutes) ||
    !isNumberOrNull(totalTimeMinutes) ||
    !isStringOrNull(yieldText) ||
    !isNumberOrNull(yieldQuantity) ||
    !isStringOrNull(yieldUnitId) ||
    status === null ||
    typeof createdAt !== 'string' ||
    typeof updatedAt !== 'string' ||
    typeof concurrencyToken !== 'string' ||
    !Array.isArray(ingredientGroups) ||
    !Array.isArray(instructionGroups) ||
    !Array.isArray(equipment) ||
    !Array.isArray(assetLinks) ||
    !Array.isArray(tags)
  ) {
    return null;
  }

  if (rawCurrentVersion !== null && !isRecord(rawCurrentVersion)) return null;
  const currentVersion = rawCurrentVersion === null ? null : decodeRecipeVersionSummary(rawCurrentVersion);
  if (rawCurrentVersion !== null && currentVersion === null) return null;

  if (rawDuplicatedFrom !== null && !isRecord(rawDuplicatedFrom)) return null;
  const duplicatedFrom = rawDuplicatedFrom === null ? null : decodeRecipeDuplicateSource(rawDuplicatedFrom);
  if (rawDuplicatedFrom !== null && duplicatedFrom === null) return null;

  const decodedIngredientGroups = ingredientGroups.map(decodeRecipeIngredientGroup);
  const decodedInstructionGroups = instructionGroups.map(decodeRecipeInstructionGroup);
  const decodedEquipment = equipment.map(decodeRecipeEquipmentItem);
  const decodedAssetLinks = assetLinks.map(decodeRecipeAssetLink);
  const decodedTags = tags.map(decodeRecipeTag);

  if (
    decodedIngredientGroups.some((group) => group === null) ||
    decodedInstructionGroups.some((group) => group === null) ||
    decodedEquipment.some((item) => item === null) ||
    decodedAssetLinks.some((link) => link === null) ||
    decodedTags.some((tag) => tag === null)
  ) {
    return null;
  }

  return {
    id,
    title,
    description,
    headnote,
    notes,
    storageNotes,
    attributionText,
    sourceUrl,
    cuisineId,
    courseId,
    primaryTechniqueId,
    prepTimeMinutes,
    cookTimeMinutes,
    restTimeMinutes,
    totalTimeMinutes,
    yieldText,
    yieldQuantity,
    yieldUnitId,
    status,
    duplicatedFrom,
    createdAt,
    updatedAt,
    concurrencyToken,
    currentVersion,
    ingredientGroups: decodedIngredientGroups as RecipeIngredientGroup[],
    instructionGroups: decodedInstructionGroups as RecipeInstructionGroup[],
    equipment: decodedEquipment as RecipeEquipmentItem[],
    assetLinks: decodedAssetLinks as RecipeAssetLink[],
    tags: decodedTags as RecipeTag[],
  };
}

// ---------------------------------------------------------------------------
// Recipe search — GET /api/v1/workspaces/{workspaceSlug}/recipes. Mirrors
// RecipeSummaryServiceModel, RecipeSearchPageServiceModel and the query parameters of
// RecipeSearchViewModel.cs. The page is cursor-paged: follow `nextCursor` until it is null rather
// than comparing item counts against a limit the server may have clamped.
// ---------------------------------------------------------------------------

export type RecipeSearchSort = 'RecentlyUpdated' | 'Title';

/** Mirrors RecipeSummaryServiceModel. No author: the API deliberately publishes no membership id. */
export interface RecipeSummary {
  readonly id: string;
  readonly title: string;
  readonly description: string | null;
  readonly status: RecipeStatus;
  readonly cuisineId: string | null;
  readonly courseId: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly latestVersionNumber: number | null;
  readonly latestVersionReadiness: RecipeVersionReadiness | null;
  /** Ingredient lines unresolved against the shared vocabulary. Never a dietary or allergen finding. */
  readonly hasUnmatchedIngredients: boolean;
}

/** Mirrors RecipeSearchPageServiceModel. `totalCount` is null when the caller declined it. */
export interface RecipeSearchPage {
  readonly items: readonly RecipeSummary[];
  readonly nextCursor: string | null;
  readonly totalCount: number | null;
}

function decodeRecipeSummary(value: unknown): RecipeSummary | null {
  if (!isRecord(value)) return null;
  const {
    id,
    title,
    description,
    status: rawStatus,
    cuisineId,
    courseId,
    createdAt,
    updatedAt,
    latestVersionNumber,
    latestVersionReadiness: rawReadiness,
    hasUnmatchedIngredients,
  } = value;

  const status = decodeEnum<RecipeStatus>(RECIPE_STATUS_VALUES, rawStatus);

  // Null is a legal readiness — a recipe with no versions — so it is distinguished from a value the
  // server sent that this client does not understand, which is a decode failure.
  const readiness =
    rawReadiness === null ? null : decodeEnum<RecipeVersionReadiness>(RECIPE_VERSION_READINESS_VALUES, rawReadiness);

  if (
    typeof id !== 'string' ||
    typeof title !== 'string' ||
    !isStringOrNull(description) ||
    status === null ||
    !isStringOrNull(cuisineId) ||
    !isStringOrNull(courseId) ||
    typeof createdAt !== 'string' ||
    typeof updatedAt !== 'string' ||
    !isNumberOrNull(latestVersionNumber) ||
    (rawReadiness !== null && readiness === null) ||
    typeof hasUnmatchedIngredients !== 'boolean'
  ) {
    return null;
  }

  return {
    id,
    title,
    description,
    status,
    cuisineId,
    courseId,
    createdAt,
    updatedAt,
    latestVersionNumber,
    latestVersionReadiness: readiness,
    hasUnmatchedIngredients,
  };
}

export function decodeRecipeSearchPage(value: unknown): RecipeSearchPage | null {
  if (!isRecord(value)) return null;
  const { items, nextCursor, totalCount } = value;

  if (!Array.isArray(items) || !isStringOrNull(nextCursor) || !isNumberOrNull(totalCount)) return null;

  const decoded = items.map(decodeRecipeSummary);
  // One unreadable row fails the page rather than silently shortening it: a library that quietly drops a
  // recipe is worse than one that reports it could not be read.
  if (decoded.some((item) => item === null)) return null;

  return { items: decoded as RecipeSummary[], nextCursor, totalCount };
}

/**
 * The filter state a library screen holds, and the only shape the typed service accepts. Deliberately a
 * subset of what the endpoint supports: cuisine and course filters need a reference-vocabulary picker that
 * does not exist yet, and `readiness`/`ingredientReview` have no control on this screen.
 */
export interface RecipeSearchQuery {
  readonly search: string;
  readonly statuses: readonly RecipeStatus[];
  readonly mine: boolean;
  readonly sort: RecipeSearchSort;
  /** The previous page's `nextCursor`, or null for the first page. */
  readonly cursor: string | null;
}

export const DEFAULT_RECIPE_SEARCH_QUERY: RecipeSearchQuery = {
  search: '',
  statuses: [],
  mine: false,
  sort: 'RecentlyUpdated',
  cursor: null,
};

/**
 * The query string for one search. Absent filters are omitted rather than sent empty, so the request says
 * what it means and two spellings of "no filter" cannot produce two different cursor scopes.
 */
export function encodeRecipeSearchQuery(query: RecipeSearchQuery): Record<string, string> {
  const params: Record<string, string> = { sort: query.sort };

  const search = query.search.trim();
  if (search.length > 0) params['search'] = search;
  if (query.statuses.length > 0) params['status'] = query.statuses.join(',');
  if (query.mine) params['mine'] = 'true';

  if (query.cursor !== null) {
    params['cursor'] = query.cursor;

    // The total spans every page and cannot change as one is turned, so it is asked for once and carried
    // forward by the caller. Counting again per page is a second query for an answer already held.
    params['includeTotal'] = 'false';
  }

  return params;
}
