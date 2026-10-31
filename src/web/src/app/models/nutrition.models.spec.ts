import {
  decodeNutritionEstimate,
  decodeNutritionMapping,
  encodeNutritionEstimateRequest,
  encodeNutritionMappingDecision,
} from './nutrition.models';

function amount(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    low: 1234.567891,
    high: 1234.567891,
    displayLow: 1235,
    displayHigh: 1235,
    displayPrecision: 0,
    rounding: 'ToEven',
    isRange: false,
    trace: false,
    ...overrides,
  };
}

function estimatePayload(): Record<string, unknown> {
  return {
    recipeId: 'r1',
    recipeVersionId: 'v4',
    versionNumber: 4,
    label: 'Estimate',
    methodVersion: 'nutrition-estimate/1',
    method: "Each counted ingredient's nutrients per 100 g, multiplied by its weight in grams and summed.",
    status: 'Incomplete',
    publishable: false,
    partial: true,
    confidence: 'CreatorPortion',
    servings: 12,
    servingsSource: 'ServingCount',
    nutrients: [
      {
        code: 'Energy',
        unit: 'Kilocalorie',
        wholeRecipe: amount(),
        perServing: amount({ low: 102.880657, high: 102.880657, displayLow: 103, displayHigh: 103 }),
        complete: true,
        linesMissingIt: [],
      },
      {
        code: 'VitaminD',
        unit: 'Microgram',
        wholeRecipe: amount({
          low: 0.004,
          high: 0.004,
          displayLow: 0,
          displayHigh: 0,
          displayPrecision: 1,
          trace: true,
        }),
        perServing: null,
        complete: false,
        linesMissingIt: ['l2'],
      },
    ],
    lines: [
      {
        lineId: 'l1',
        displayText: '250 g butter',
        outcome: 'Included',
        reason: 'None',
        required: true,
        gramsLow: 250,
        gramsHigh: 250,
        basis: 'Mass',
        mappingId: 'm1',
        source: { sourceCode: 'fdc', externalRecordId: '173410', sourceVersion: '2026-01-01' },
      },
      {
        lineId: 'l2',
        displayText: '1 cup oat milk',
        outcome: 'Unmapped',
        reason: 'NoMapping',
        required: true,
        gramsLow: null,
        gramsHigh: null,
        basis: null,
        mappingId: null,
        source: null,
      },
    ],
    sources: [
      {
        sourceCode: 'fdc',
        citation: 'USDA FoodData Central',
        licenseCode: 'CC0-1.0',
        licenseUrl: 'https://example.test/cc0',
      },
    ],
    unresolved: { unmapped: ['l2'], needsReview: [], unconvertible: [], notQuantified: [] },
  };
}

function mappingPayload(): Record<string, unknown> {
  return {
    id: 'm1',
    ingredientId: 'i1',
    status: 'Candidate',
    matchMethod: 'AutomaticSearch',
    confidence: 'Low',
    reference: {
      sourceCode: 'fdc',
      externalRecordId: '173410',
      description: 'Butter, salted',
      sourceVersion: '2026-01-01',
    },
    reviewSourceVersion: null,
    unresolvedReason: null,
    portion: { measurementUnitId: 'u1', amount: 1, grams: 227, label: 'cup', origin: 'Reference' },
    rejectedReferenceCount: 2,
    confirmedAt: null,
    updatedAt: '2026-10-19T09:00:00Z',
    concurrencyToken: 'token-1',
  };
}

describe('nutrition.models', () => {
  describe('the estimate', () => {
    it('decodes an estimate whole, keeping the server label, method and both display and exact figures', () => {
      const estimate = decodeNutritionEstimate(estimatePayload());

      expect(estimate).not.toBeNull();
      expect(estimate?.label).toBe('Estimate');
      expect(estimate?.methodVersion).toBe('nutrition-estimate/1');
      expect(estimate?.status).toBe('Incomplete');
      expect(estimate?.publishable).toBeFalse();
      expect(estimate?.partial).toBeTrue();
      expect(estimate?.confidence).toBe('CreatorPortion');
      expect(estimate?.nutrients[0].wholeRecipe.displayLow).toBe(1235);
      expect(estimate?.nutrients[0].wholeRecipe.low).toBe(1234.567891);
      expect(estimate?.unresolved.unmapped).toEqual(['l2']);
    });

    it('keeps a trace figure as a trace rather than as a zero', () => {
      const estimate = decodeNutritionEstimate(estimatePayload());
      const vitaminD = estimate?.nutrients[1];

      expect(vitaminD?.wholeRecipe.trace).toBeTrue();
      expect(vitaminD?.wholeRecipe.displayLow).toBe(0);
      expect(vitaminD?.wholeRecipe.low).toBe(0.004);
    });

    it('keeps an explicitly absent per-serving figure apart from an unreadable one', () => {
      const absent = decodeNutritionEstimate(estimatePayload());
      expect(absent?.nutrients[1].perServing).toBeNull();

      const broken = estimatePayload();
      (broken['nutrients'] as Record<string, unknown>[])[0]['perServing'] = { low: 1 };
      expect(decodeNutritionEstimate(broken)).toBeNull();
    });

    it('decodes a line that was not counted, with its reason and no grams', () => {
      const line = decodeNutritionEstimate(estimatePayload())?.lines[1];

      expect(line?.outcome).toBe('Unmapped');
      expect(line?.reason).toBe('NoMapping');
      expect(line?.required).toBeTrue();
      expect(line?.gramsLow).toBeNull();
      expect(line?.basis).toBeNull();
      expect(line?.source).toBeNull();
    });

    it('sends the yield basis as given, so an omitted one still reads as the version’s own', () => {
      expect(encodeNutritionEstimateRequest({ servings: 12 })).toEqual({ servings: 12 });
      expect(encodeNutritionEstimateRequest({ servings: null })).toEqual({ servings: null });
    });

    // Whole or nothing: a dropped nutrient or line reads as one that was counted.
    it('refuses the estimate when an outcome, reason or nutrient code is a word it does not know', () => {
      for (const [path, field, value] of [
        ['lines', 'outcome', 'Guessed'],
        ['lines', 'reason', 'Whatever'],
        ['nutrients', 'code', 'Caffeine'],
        ['nutrients', 'unit', 'Ounce'],
      ] as const) {
        const raw = estimatePayload();
        (raw[path] as Record<string, unknown>[])[0][field] = value;
        expect(decodeNutritionEstimate(raw)).withContext(`${path}.${field}`).toBeNull();
      }
    });

    it('refuses the estimate when the status, confidence or servings source is unknown', () => {
      for (const field of ['status', 'confidence', 'servingsSource'] as const) {
        const raw = estimatePayload();
        raw[field] = 'Something';
        expect(decodeNutritionEstimate(raw)).withContext(field).toBeNull();
      }
    });

    it('refuses an estimate that arrives without its label or its method', () => {
      const unlabelled = estimatePayload();
      unlabelled['label'] = '  ';
      expect(decodeNutritionEstimate(unlabelled)).toBeNull();

      const methodless = estimatePayload();
      delete methodless['method'];
      expect(decodeNutritionEstimate(methodless)).toBeNull();
    });

    it('refuses an amount that is missing a part of itself', () => {
      const raw = estimatePayload();
      (raw['nutrients'] as Record<string, unknown>[])[0]['wholeRecipe'] = amount({ rounding: 'Sideways' });

      expect(decodeNutritionEstimate(raw)).toBeNull();
    });

    it('refuses the unresolved groups when they are not three lists of ids', () => {
      const raw = estimatePayload();
      raw['unresolved'] = { unmapped: ['l2'], needsReview: [] };

      expect(decodeNutritionEstimate(raw)).toBeNull();
    });

    it('refuses anything that is not an object at all', () => {
      expect(decodeNutritionEstimate(null)).toBeNull();
      expect(decodeNutritionEstimate('Estimate')).toBeNull();
    });
  });

  describe('the mapping', () => {
    it('decodes a proposal with its reference, confidence, portion and token', () => {
      const mapping = decodeNutritionMapping(mappingPayload());

      expect(mapping?.status).toBe('Candidate');
      expect(mapping?.matchMethod).toBe('AutomaticSearch');
      expect(mapping?.confidence).toBe('Low');
      expect(mapping?.reference?.description).toBe('Butter, salted');
      expect(mapping?.portion?.origin).toBe('Reference');
      expect(mapping?.rejectedReferenceCount).toBe(2);
      expect(mapping?.concurrencyToken).toBe('token-1');
    });

    it('decodes an unresolved mapping, which has no reference and a reason instead', () => {
      const raw = mappingPayload();
      raw['status'] = 'Unresolved';
      raw['reference'] = null;
      raw['portion'] = null;
      raw['matchMethod'] = null;
      raw['confidence'] = null;
      raw['unresolvedReason'] = 'CreatorDeclined';

      const mapping = decodeNutritionMapping(raw);
      expect(mapping?.status).toBe('Unresolved');
      expect(mapping?.reference).toBeNull();
      expect(mapping?.unresolvedReason).toBe('CreatorDeclined');
    });

    it('refuses a mapping whose status, confidence or reason is a word it does not know', () => {
      for (const field of ['status', 'confidence', 'matchMethod', 'unresolvedReason'] as const) {
        const raw = mappingPayload();
        raw[field] = 'Something';
        expect(decodeNutritionMapping(raw)).withContext(field).toBeNull();
      }
    });

    // Nothing is decided without a token to quote back, so one that arrives empty is unusable.
    it('refuses a mapping with no concurrency token', () => {
      const raw = mappingPayload();
      raw['concurrencyToken'] = '';

      expect(decodeNutritionMapping(raw)).toBeNull();
    });

    it('refuses a portion that is missing its grams or its origin', () => {
      const raw = mappingPayload();
      raw['portion'] = { measurementUnitId: 'u1', amount: 1, label: 'cup', origin: 'Reference' };

      expect(decodeNutritionMapping(raw)).toBeNull();
    });

    it('sends only the token on a decision', () => {
      expect(encodeNutritionMappingDecision({ expectedConcurrencyToken: 'token-1' })).toEqual({
        expectedConcurrencyToken: 'token-1',
      });
    });
  });
});
