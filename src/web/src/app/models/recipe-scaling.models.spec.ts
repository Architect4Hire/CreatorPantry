import {
  UNKNOWN_SCALING_WARNING,
  decodeRecipeScalingResult,
  encodeScaleRecipeRequest,
} from './recipe-scaling.models';

/** One complete wire payload, shaped exactly as RecipeScalingResultServiceModel serializes. */
function payload(): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    preview: {
      factor: { numerator: '2', denominator: '1' },
      requestedTargetYieldQuantity: null,
      scaledYieldQuantity: { numerator: '24', denominator: '1' },
      scaledYieldDisplayQuantity: 24,
      lines: [
        {
          id: 'line-1',
          displayText: '250 g all-purpose flour',
          wasScaled: true,
          scaledQuantity: { numerator: '500', denominator: '1' },
          scaledQuantityUpper: null,
          scaledDisplayQuantity: 500,
          scaledDisplayQuantityUpper: null,
          warnings: [],
        },
        {
          id: 'line-2',
          displayText: 'salt, to taste',
          wasScaled: false,
          scaledQuantity: null,
          scaledQuantityUpper: null,
          scaledDisplayQuantity: null,
          scaledDisplayQuantityUpper: null,
          warnings: ['ReviewRequiredQualitative'],
        },
      ],
      recipeWarnings: ['ExtremeScaleFactor'],
    },
  };
}

describe('recipe-scaling.models', () => {
  describe('decodeRecipeScalingResult', () => {
    it('decodes a complete preview, keeping exact quantities as fractions', () => {
      const result = decodeRecipeScalingResult(payload());

      expect(result).not.toBeNull();
      expect(result!.sourceVersionNumber).toBe(3);
      expect(result!.preview.factor).toEqual({ numerator: '2', denominator: '1' });
      expect(result!.preview.scaledYieldDisplayQuantity).toBe(24);
      expect(result!.preview.recipeWarnings).toEqual(['ExtremeScaleFactor']);
      expect(result!.preview.lines.length).toBe(2);
      expect(result!.preview.lines[0].scaledQuantity).toEqual({ numerator: '500', denominator: '1' });
    });

    it('keeps a line with no quantity at all', () => {
      const result = decodeRecipeScalingResult(payload());

      expect(result!.preview.lines[1].scaledQuantity).toBeNull();
      expect(result!.preview.lines[1].scaledDisplayQuantity).toBeNull();
      expect(result!.preview.lines[1].wasScaled).toBeFalse();
    });

    // A caution the server raised must not disappear because this build shipped before the code existed.
    it('keeps an unrecognised warning code rather than dropping it', () => {
      const raw = payload();
      const preview = raw['preview'] as Record<string, unknown>;
      preview['recipeWarnings'] = ['ExtremeScaleFactor', 'SomethingAddedLater'];

      const result = decodeRecipeScalingResult(raw);

      expect(result).not.toBeNull();
      expect(result!.preview.recipeWarnings).toEqual(['ExtremeScaleFactor', UNKNOWN_SCALING_WARNING]);
    });

    it('keeps an unrecognised warning on a line too', () => {
      const raw = payload();
      const lines = (raw['preview'] as Record<string, unknown>)['lines'] as Record<string, unknown>[];
      lines[0]['warnings'] = ['SomethingAddedLater'];

      const result = decodeRecipeScalingResult(raw);

      expect(result!.preview.lines[0].warnings).toEqual([UNKNOWN_SCALING_WARNING]);
    });

    it('refuses a payload whose factor is not a quantity', () => {
      const raw = payload();
      (raw['preview'] as Record<string, unknown>)['factor'] = 2;

      expect(decodeRecipeScalingResult(raw)).toBeNull();
    });

    // A present-but-unreadable quantity is not the same as an absent one: collapsing the two would render a
    // malformed amount as a line that simply has none.
    it('refuses a line whose quantity is present but unreadable', () => {
      const raw = payload();
      const lines = (raw['preview'] as Record<string, unknown>)['lines'] as Record<string, unknown>[];
      lines[0]['scaledQuantity'] = { numerator: 500 };

      expect(decodeRecipeScalingResult(raw)).toBeNull();
    });

    it('refuses a warning that is not a string', () => {
      const raw = payload();
      (raw['preview'] as Record<string, unknown>)['recipeWarnings'] = [8];

      expect(decodeRecipeScalingResult(raw)).toBeNull();
    });

    it('refuses a payload with no source version number', () => {
      const raw = payload();
      delete raw['sourceVersionNumber'];

      expect(decodeRecipeScalingResult(raw)).toBeNull();
    });

    it('refuses a payload that is not an object', () => {
      expect(decodeRecipeScalingResult(null)).toBeNull();
      expect(decodeRecipeScalingResult('scaled')).toBeNull();
    });
  });

  describe('encodeScaleRecipeRequest', () => {
    // The server accepts exactly one of the two. Sending the other as null would still pass its
    // exclusive-or, but a request that names a field it does not mean is a request that reads wrong.
    it('sends only the multiplier when scaling by a factor', () => {
      expect(encodeScaleRecipeRequest({ sourceVersionNumber: 2, multiplier: 1.5 })).toEqual({
        sourceVersionNumber: 2,
        multiplier: 1.5,
      });
    });

    it('sends only the target yield when scaling to a yield', () => {
      expect(encodeScaleRecipeRequest({ sourceVersionNumber: 2, targetYieldQuantity: 24 })).toEqual({
        sourceVersionNumber: 2,
        targetYieldQuantity: 24,
      });
    });
  });
});
