import {
  decodeRecipeTemperatureConversionResult,
  decodeRecipeUnitConversionResult,
  encodeConvertTemperatureRequest,
  encodeConvertUnitsRequest,
} from './recipe-conversion.models';

function unitPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    result: {
      method: 'SameDimensionFactor',
      convertedQuantity: { numerator: '1', denominator: '4' },
      convertedDisplayQuantity: 0.25,
      precision: 2,
      rounding: 'ToEven',
      formula: '× (1 gram-per-base ÷ 1000 kilogram-per-base)',
      source: null,
      ...overrides,
    },
  };
}

function temperaturePayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    result: {
      sourceValue: { numerator: '180', denominator: '1' },
      sourceScale: 'Celsius',
      convertedValue: { numerator: '356', denominator: '1' },
      convertedDisplayValue: 356,
      convertedScale: 'Fahrenheit',
      precision: 0,
      rounding: 'ToEven',
      formula: '°F = °C × 9/5 + 32',
      ovenModeContext: null,
      safetyNote: null,
      ...overrides,
    },
  };
}

describe('recipe-conversion.models', () => {
  describe('decodeRecipeUnitConversionResult', () => {
    it('decodes a same-dimension conversion', () => {
      const decoded = decodeRecipeUnitConversionResult(unitPayload());

      expect(decoded?.sourceVersionNumber).toBe(3);
      expect(decoded?.result.method).toBe('SameDimensionFactor');
      expect(decoded?.result.convertedDisplayQuantity).toBe(0.25);
      expect(decoded?.result.convertedQuantity).toEqual({ numerator: '1', denominator: '4' });
      expect(decoded?.result.rounding).toBe('ToEven');
      expect(decoded?.result.source).toBeNull();
    });

    it('decodes a density conversion and keeps its citation', () => {
      const decoded = decodeRecipeUnitConversionResult(
        unitPayload({ method: 'IngredientDensity', source: 'USDA FoodData Central, entry 169761' }),
      );

      expect(decoded?.result.method).toBe('IngredientDensity');
      expect(decoded?.result.source).toBe('USDA FoodData Central, entry 169761');
    });

    it('refuses a method it does not recognise', () => {
      expect(decodeRecipeUnitConversionResult(unitPayload({ method: 'Guesswork' }))).toBeNull();
    });

    // Rounding is one of the facts CALC-002 asks a conversion to publish. A mode this build cannot name is
    // not something to render as though it were the default.
    it('refuses a rounding mode it does not recognise', () => {
      expect(decodeRecipeUnitConversionResult(unitPayload({ rounding: 'Stochastic' }))).toBeNull();
    });

    it('refuses a result with no formula', () => {
      const raw = unitPayload();
      delete (raw['result'] as Record<string, unknown>)['formula'];

      expect(decodeRecipeUnitConversionResult(raw)).toBeNull();
    });

    it('refuses a value that is not an object', () => {
      expect(decodeRecipeUnitConversionResult(null)).toBeNull();
    });
  });

  describe('decodeRecipeTemperatureConversionResult', () => {
    it('decodes a conversion and both echoed context fields', () => {
      const decoded = decodeRecipeTemperatureConversionResult(
        temperaturePayload({ ovenModeContext: 'convection', safetyNote: 'until the centre reads 74 °C' }),
      );

      expect(decoded?.result.convertedDisplayValue).toBe(356);
      expect(decoded?.result.convertedScale).toBe('Fahrenheit');
      expect(decoded?.result.formula).toBe('°F = °C × 9/5 + 32');
      expect(decoded?.result.ovenModeContext).toBe('convection');
      expect(decoded?.result.safetyNote).toBe('until the centre reads 74 °C');
    });

    it('decodes absent context as null rather than as an empty string', () => {
      const decoded = decodeRecipeTemperatureConversionResult(temperaturePayload());

      expect(decoded?.result.ovenModeContext).toBeNull();
      expect(decoded?.result.safetyNote).toBeNull();
    });

    it('refuses a scale it does not recognise', () => {
      expect(decodeRecipeTemperatureConversionResult(temperaturePayload({ convertedScale: 'Kelvin' }))).toBeNull();
    });

    it('refuses a source value that is not an exact quantity', () => {
      expect(decodeRecipeTemperatureConversionResult(temperaturePayload({ sourceValue: 180 }))).toBeNull();
    });
  });

  describe('encoders', () => {
    it('sends every unit-conversion field the route names', () => {
      expect(
        encodeConvertUnitsRequest({
          sourceVersionNumber: 3,
          quantity: 1000,
          fromUnitId: 'unit-gram',
          toUnitId: 'unit-kilogram',
        }),
      ).toEqual({ sourceVersionNumber: 3, quantity: 1000, fromUnitId: 'unit-gram', toUnitId: 'unit-kilogram' });
    });

    // Null, not omitted: the route echoes both fields back, and a null in has to be a null out.
    it('sends absent temperature context as null', () => {
      expect(
        encodeConvertTemperatureRequest({
          sourceVersionNumber: 3,
          value: 180,
          fromScale: 'Celsius',
          toScale: 'Fahrenheit',
          precision: 0,
          ovenModeContext: null,
          safetyNote: null,
        }),
      ).toEqual({
        sourceVersionNumber: 3,
        value: 180,
        fromScale: 'Celsius',
        toScale: 'Fahrenheit',
        precision: 0,
        ovenModeContext: null,
        safetyNote: null,
      });
    });
  });
});
