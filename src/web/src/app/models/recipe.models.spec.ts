import {
  CreatedRecipe,
  RecipeDetail,
  RecipeIngredient,
  RecipeInstructionStep,
  absent,
  decodeCreatedRecipe,
  decodeRecipeDetail,
  encodeCreateRecipeRequest,
  encodeUpdateRecipeRequest,
  submitted,
} from './recipe.models';

const VALID_CREATED_RECIPE: CreatedRecipe = {
  recipeId: 'r1',
  title: 'Chili',
  status: 'Ready',
  versionId: 'v1',
  versionNumber: 1,
  createdAt: '2026-01-01T00:00:00Z',
};

const VALID_INGREDIENT: RecipeIngredient = {
  id: 'ing1',
  sortOrder: 0,
  displayText: '2 cups flour',
  ingredientNameText: 'flour',
  quantity: 2,
  quantityUpper: null,
  measurementUnitId: 'unit1',
  ingredientId: 'ref1',
  matchStatus: 'Matched',
  preparationNote: null,
  isOptional: false,
  scalingBehavior: 'Proportional',
};

const VALID_STEP: RecipeInstructionStep = {
  id: 'step1',
  sortOrder: 0,
  text: 'Mix everything.',
  techniqueId: null,
  durationMinutes: null,
  temperatureValue: null,
  temperatureUnitId: null,
  note: null,
};

const VALID_RECIPE_DETAIL: RecipeDetail = {
  id: 'r1',
  title: 'Chili',
  description: null,
  headnote: null,
  notes: null,
  storageNotes: null,
  attributionText: null,
  sourceUrl: null,
  cuisineId: null,
  courseId: null,
  primaryTechniqueId: null,
  prepTimeMinutes: 10,
  cookTimeMinutes: 20,
  restTimeMinutes: null,
  totalTimeMinutes: 30,
  yieldText: '4 servings',
  yieldQuantity: 4,
  yieldUnitId: null,
  status: 'Draft',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  concurrencyToken: 'AAAAAAAAB9E=',
  currentVersion: { id: 'v1', versionNumber: 1, source: 'CreatorEdit', readiness: 'Draft', reason: null, createdAt: '2026-01-01T00:00:00Z' },
  ingredientGroups: [{ id: 'g1', title: null, sortOrder: 0, ingredients: [VALID_INGREDIENT] }],
  instructionGroups: [{ id: 'ig1', title: null, sortOrder: 0, steps: [VALID_STEP] }],
  equipment: [],
  assetLinks: [],
  tags: [{ workspaceTagId: 't1', name: 'quick' }],
};

describe('decodeCreatedRecipe', () => {
  it('decodes a valid response with the wire\'s PascalCase status name', () => {
    expect(decodeCreatedRecipe(VALID_CREATED_RECIPE)).toEqual(VALID_CREATED_RECIPE);
  });

  it('rejects an unknown status string', () => {
    expect(decodeCreatedRecipe({ ...VALID_CREATED_RECIPE, status: 'NotARealStatus' })).toBeNull();
  });

  it('rejects a numeric status (the wire never sends the underlying integer)', () => {
    expect(decodeCreatedRecipe({ ...VALID_CREATED_RECIPE, status: 1 })).toBeNull();
  });

  it('rejects a missing required field', () => {
    const { recipeId, ...withoutRecipeId } = VALID_CREATED_RECIPE;
    expect(decodeCreatedRecipe(withoutRecipeId)).toBeNull();
  });

  it('rejects non-object input', () => {
    for (const value of [null, undefined, 'x', 42, []]) {
      expect(decodeCreatedRecipe(value)).withContext(JSON.stringify(value)).toBeNull();
    }
  });
});

describe('decodeRecipeDetail', () => {
  it('decodes a full valid recipe, including nested groups and enums as PascalCase strings', () => {
    expect(decodeRecipeDetail(VALID_RECIPE_DETAIL)).toEqual(VALID_RECIPE_DETAIL);
  });

  it('decodes a recipe with no current version', () => {
    const result = decodeRecipeDetail({ ...VALID_RECIPE_DETAIL, currentVersion: null });
    expect(result?.currentVersion).toBeNull();
  });

  it('rejects a missing required top-level field', () => {
    const { concurrencyToken, ...withoutToken } = VALID_RECIPE_DETAIL;
    expect(decodeRecipeDetail(withoutToken)).toBeNull();
  });

  it('rejects an unknown recipe status string', () => {
    expect(decodeRecipeDetail({ ...VALID_RECIPE_DETAIL, status: 'NotARealStatus' })).toBeNull();
  });

  it('rejects a malformed nested ingredient (unknown matchStatus)', () => {
    const malformed = {
      ...VALID_RECIPE_DETAIL,
      ingredientGroups: [{ id: 'g1', title: null, sortOrder: 0, ingredients: [{ ...VALID_INGREDIENT, matchStatus: 'NotARealStatus' }] }],
    };
    expect(decodeRecipeDetail(malformed)).toBeNull();
  });

  it('rejects a malformed nested instruction step (wrong type)', () => {
    const malformed = {
      ...VALID_RECIPE_DETAIL,
      instructionGroups: [{ id: 'ig1', title: null, sortOrder: 0, steps: [{ ...VALID_STEP, text: 42 }] }],
    };
    expect(decodeRecipeDetail(malformed)).toBeNull();
  });

  it('rejects a malformed nested equipment item (wrong type)', () => {
    const malformed = {
      ...VALID_RECIPE_DETAIL,
      equipment: [{ id: 'eq1', sortOrder: 0, displayText: 'Dutch oven', equipmentTypeId: null, isOptional: 'yes', note: null }],
    };
    expect(decodeRecipeDetail(malformed)).toBeNull();
  });

  it('rejects a malformed nested asset link (unknown role)', () => {
    const malformed = {
      ...VALID_RECIPE_DETAIL,
      assetLinks: [{ id: 'a1', sortOrder: 0, mediaAssetId: 'm1', role: 'NotARealRole', caption: null }],
    };
    expect(decodeRecipeDetail(malformed)).toBeNull();
  });

  it('rejects a malformed nested tag (missing name)', () => {
    const malformed = {
      ...VALID_RECIPE_DETAIL,
      tags: [{ workspaceTagId: 't1' }],
    };
    expect(decodeRecipeDetail(malformed)).toBeNull();
  });

  it('rejects non-object input', () => {
    for (const value of [null, undefined, 'x', 42, []]) {
      expect(decodeRecipeDetail(value)).withContext(JSON.stringify(value)).toBeNull();
    }
  });
});

describe('encodeCreateRecipeRequest', () => {
  it('passes fields through unchanged when status is omitted', () => {
    expect(encodeCreateRecipeRequest({ title: 'Chili' })).toEqual({ title: 'Chili' });
  });

  it('passes status through as its wire PascalCase name, unconverted', () => {
    expect(encodeCreateRecipeRequest({ title: 'Chili', status: 'Ready' })).toEqual({ title: 'Chili', status: 'Ready' });
  });

  it('passes instructions through unchanged, with no id on any group or step', () => {
    const instructions = [{ title: 'Batter', steps: [{ text: 'Mix.' }, { text: 'Pour.' }] }];
    expect(encodeCreateRecipeRequest({ title: 'Chili', instructions })).toEqual({ title: 'Chili', instructions });
  });
});

describe('encodeUpdateRecipeRequest', () => {
  it('includes only the concurrency token when nothing else is submitted', () => {
    expect(encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1' })).toEqual({ expectedConcurrencyToken: 'tok1' });
  });

  it('omits an absent field entirely (leave-unchanged semantics)', () => {
    const body = encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1', title: absent() });
    expect(body).toEqual({ expectedConcurrencyToken: 'tok1' });
    expect('title' in body).toBeFalse();
  });

  it('includes a submitted field, even when its value is null (clear semantics)', () => {
    const body = encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1', description: submitted(null), tags: submitted(null) });
    expect(body).toEqual({ expectedConcurrencyToken: 'tok1', description: null, tags: null });
  });

  it('includes a submitted status as its wire PascalCase name, unconverted', () => {
    expect(encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1', status: submitted('Archived') })).toEqual({
      expectedConcurrencyToken: 'tok1',
      status: 'Archived',
    });
  });

  it('includes reason only when provided', () => {
    expect(encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1', reason: 'Fixing a typo' })).toEqual({
      expectedConcurrencyToken: 'tok1',
      reason: 'Fixing a typo',
    });
  });

  it('includes submitted instructions with ids on existing groups/steps, none on new ones', () => {
    const instructions = [
      { id: 'g1', title: 'Batter', steps: [{ id: 's1', text: 'Mix well.' }] },
      { steps: [{ text: 'Bake.' }] },
    ];
    const body = encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1', instructions: submitted(instructions) });
    expect(body).toEqual({ expectedConcurrencyToken: 'tok1', instructions });
  });

  it('omits instructions entirely when not submitted (leaves the method unchanged)', () => {
    const body = encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1', title: submitted('New title') });
    expect('instructions' in body).toBeFalse();
  });

  it('submitting an empty instructions list clears every group (distinct from omitting the field)', () => {
    const body = encodeUpdateRecipeRequest({ expectedConcurrencyToken: 'tok1', instructions: submitted([]) });
    expect(body).toEqual({ expectedConcurrencyToken: 'tok1', instructions: [] });
  });
});
