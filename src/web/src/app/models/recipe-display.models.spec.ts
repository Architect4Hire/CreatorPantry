import {
  FractionPresentation,
  decodeRecipeQuantityDisplayResult,
  encodeNormalizeDisplayRequest,
} from './recipe-display.models';

function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    result: {
      text: '1½ cups',
      presentation: 'Fraction',
      upperPresentation: null,
      precision: 2,
      rounding: 'ToEven',
      ...overrides,
    },
  };
}

describe('recipe-display.models', () => {
  describe('decodeRecipeQuantityDisplayResult', () => {
    it('decodes a rendered single value', () => {
      const decoded = decodeRecipeQuantityDisplayResult(payload());

      expect(decoded?.sourceVersionNumber).toBe(3);
      expect(decoded?.result.text).toBe('1½ cups');
      expect(decoded?.result.presentation).toBe('Fraction');
      expect(decoded?.result.upperPresentation).toBeNull();
      expect(decoded?.result.rounding).toBe('ToEven');
    });

    it('decodes each presentation the server can return', () => {
      const presentations: readonly FractionPresentation[] = ['Whole', 'Fraction', 'Decimal'];
      for (const presentation of presentations) {
        expect(decodeRecipeQuantityDisplayResult(payload({ presentation }))?.result.presentation).toBe(
          presentation,
        );
      }
    });

    // A range's two bounds can be rendered differently, so the upper one is its own fact.
    it('decodes a range, keeping the upper bound presentation', () => {
      const decoded = decodeRecipeQuantityDisplayResult(
        payload({ text: '1–1½ cups', presentation: 'Whole', upperPresentation: 'Fraction' }),
      );

      expect(decoded?.result.text).toBe('1–1½ cups');
      expect(decoded?.result.presentation).toBe('Whole');
      expect(decoded?.result.upperPresentation).toBe('Fraction');
    });

    it('refuses a presentation it does not recognise', () => {
      expect(decodeRecipeQuantityDisplayResult(payload({ presentation: 'Vulgar' }))).toBeNull();
    });

    it('refuses an upper presentation it does not recognise', () => {
      expect(decodeRecipeQuantityDisplayResult(payload({ upperPresentation: 'Vulgar' }))).toBeNull();
    });

    // Rounding is one of the facts CALC-002 asks a rendering to publish.
    it('refuses a rounding mode it does not recognise', () => {
      expect(decodeRecipeQuantityDisplayResult(payload({ rounding: 'Stochastic' }))).toBeNull();
    });

    it('refuses a result with no text', () => {
      const raw = payload();
      delete (raw['result'] as Record<string, unknown>)['text'];

      expect(decodeRecipeQuantityDisplayResult(raw)).toBeNull();
    });

    it('refuses a value that is not an object', () => {
      expect(decodeRecipeQuantityDisplayResult('1½ cups')).toBeNull();
    });
  });

  describe('encodeNormalizeDisplayRequest', () => {
    it('sends every field the route names for a single value', () => {
      expect(
        encodeNormalizeDisplayRequest({
          sourceVersionNumber: 3,
          value: 1.5,
          upperValue: null,
          unitId: 'unit-cup',
          precision: 2,
          useAbbreviation: false,
          rounding: 'ToEven',
        }),
      ).toEqual({
        sourceVersionNumber: 3,
        value: 1.5,
        unitId: 'unit-cup',
        precision: 2,
        useAbbreviation: false,
        rounding: 'ToEven',
      });
    });

    // The validator only compares an upper bound it was actually given, so a value-only request must not
    // name the field at all.
    it('names the upper value only when there is a range', () => {
      const body = encodeNormalizeDisplayRequest({
        sourceVersionNumber: 3,
        value: 1,
        upperValue: 1.5,
        unitId: 'unit-cup',
        precision: 2,
        useAbbreviation: true,
        rounding: 'AwayFromZero',
      });

      expect(body['upperValue']).toBe(1.5);
      expect(body['useAbbreviation']).toBeTrue();
      expect(body['rounding']).toBe('AwayFromZero');
    });
  });
});
