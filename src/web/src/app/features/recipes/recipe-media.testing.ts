// Fixtures shared by the recipe media specs. Test-only: nothing outside a `.spec.ts` imports this.

import { DamAssetDetail, DamAssetSummary, DamAssetVersion } from '../../models/dam-asset.models';
import { RecipeMediaStep } from '../../models/recipe-asset-link.models';
import { RecipeAssetLink, RecipeDetail } from '../../models/recipe.models';

export function mediaLink(overrides: Partial<RecipeAssetLink> = {}): RecipeAssetLink {
  return {
    id: 'l1',
    sortOrder: 0,
    mediaAssetId: 'a1',
    mediaAssetVersionNumber: null,
    instructionStepId: null,
    role: 'Gallery',
    caption: null,
    ...overrides,
  };
}

export const MEDIA_STEPS: readonly RecipeMediaStep[] = [
  { id: 's1', number: 1, text: 'Cream the butter and sugar.' },
  { id: 's2', number: 2, text: 'Fold in the flour.' },
];

export function mediaRecipe(overrides: Partial<RecipeDetail> = {}): RecipeDetail {
  return {
    id: 'r1',
    title: 'Olive oil cake',
    description: null,
    headnote: null,
    notes: null,
    storageNotes: null,
    attributionText: null,
    sourceUrl: null,
    cuisineId: null,
    courseId: null,
    primaryTechniqueId: null,
    prepTimeMinutes: null,
    cookTimeMinutes: null,
    restTimeMinutes: null,
    totalTimeMinutes: null,
    yieldText: null,
    yieldQuantity: null,
    yieldUnitId: null,
    servingCount: null,
    servingSize: null,
    status: 'Draft',
    duplicatedFrom: null,
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-08T00:00:00Z',
    concurrencyToken: 'AAAAAAAAB9I=',
    currentVersion: { id: 'v2', versionNumber: 2, source: 'CreatorEdit', readiness: 'Draft', reason: null, createdAt: '2026-10-08T00:00:00Z' },
    ingredientGroups: [],
    instructionGroups: [
      {
        id: 'ig1',
        title: null,
        sortOrder: 0,
        steps: MEDIA_STEPS.map((step, index) => ({
          id: step.id,
          sortOrder: index,
          text: step.text,
          techniqueId: null,
          durationMinutes: null,
          temperatureValue: null,
          temperatureUnitId: null,
          note: null,
        })),
      },
    ],
    equipment: [],
    assetLinks: [mediaLink()],
    tags: [],
    ...overrides,
  };
}

export function assetVersion(versionNumber: number): DamAssetVersion {
  return {
    versionNumber,
    mediaType: 'image/jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 204800,
    originalFileName: `IMG_00${versionNumber}.JPG`,
    source: 'Upload',
    createdAt: '2026-10-08T12:00:00+00:00',
  };
}

export function assetSummary(overrides: Partial<DamAssetSummary> = {}): DamAssetSummary {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    description: null,
    kind: 'Original',
    altText: 'A round loaf on a linen cloth.',
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    cuisineId: null,
    courseId: null,
    currentVersionNumber: 1,
    mediaType: 'image/jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 204800,
    utilizationCount: 0,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

/** An asset with `versions` versions, the highest of them current. */
export function assetDetail(overrides: Partial<DamAssetDetail> = {}, versions = 1): DamAssetDetail {
  const all = Array.from({ length: versions }, (_, index) => assetVersion(versions - index));

  return {
    id: 'a1',
    title: 'Soda bread hero',
    description: null,
    altText: 'A round loaf on a linen cloth.',
    kind: 'Original',
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    rightsHolder: null,
    attributionText: null,
    tags: [],
    currentVersion: all[0] ?? null,
    versions: all,
    utilizationCount: 0,
    recipeLinks: [],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [],
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    concurrencyToken: 'asset-token-1',
    ...overrides,
  };
}
