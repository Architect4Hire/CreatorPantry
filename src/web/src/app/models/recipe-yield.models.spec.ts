import {
  decodeRecipeYieldReconciliationResult,
  encodeRecalculateYieldRequest,
} from './recipe-yield.models';

function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    preview: {
      status: 'Reconciled',
      batchYield: 12,
      servingCount: 6,
      servingSize: 2,
      solvedField: null,
      formula: 'servingCount × servingSize',
      panVolume: null,
      panFillRatio: null,
      panComparable: null,
      ...overrides,
    },
  };
}

describe('recipe-yield.models', () => {
  describe('decodeRecipeYieldReconciliationResult', () => {
    it('decodes a reconciled preview', () => {
      const decoded = decodeRecipeYieldReconciliationResult(payload());

      expect(decoded?.sourceVersionNumber).toBe(3);
      expect(decoded?.preview.status).toBe('Reconciled');
      expect(decoded?.preview.batchYield).toBe(12);
      expect(decoded?.preview.solvedField).toBeNull();
    });

    it('decodes a solved preview and which field it computed', () => {
      const decoded = decodeRecipeYieldReconciliationResult(
        payload({ status: 'Solved', solvedField: 'BatchYield' }),
      );

      expect(decoded?.preview.status).toBe('Solved');
      expect(decoded?.preview.solvedField).toBe('BatchYield');
    });

    // Neither of these is a failed request: both are conclusions about the recipe's own figures.
    it('decodes a contradictory preview with all three values intact', () => {
      const decoded = decodeRecipeYieldReconciliationResult(
        payload({ status: 'Contradictory', batchYield: 99 }),
      );

      expect(decoded?.preview.status).toBe('Contradictory');
      expect(decoded?.preview.batchYield).toBe(99);
      expect(decoded?.preview.servingCount).toBe(6);
      expect(decoded?.preview.servingSize).toBe(2);
    });

    it('decodes an insufficient-input preview, whose formula is null', () => {
      const decoded = decodeRecipeYieldReconciliationResult(
        payload({ status: 'InsufficientInput', servingCount: null, servingSize: null, formula: null }),
      );

      expect(decoded?.preview.status).toBe('InsufficientInput');
      expect(decoded?.preview.formula).toBeNull();
    });

    // Three states, not two: no pan given, a pan that could not be compared, and a computed ratio.
    it('decodes all three pan states', () => {
      expect(decodeRecipeYieldReconciliationResult(payload())?.preview.panComparable).toBeNull();

      expect(
        decodeRecipeYieldReconciliationResult(payload({ panVolume: 9, panComparable: false }))?.preview
          .panComparable,
      ).toBeFalse();

      const compared = decodeRecipeYieldReconciliationResult(
        payload({ panVolume: 9, panComparable: true, panFillRatio: { numerator: '4', denominator: '3' } }),
      );
      expect(compared?.preview.panComparable).toBeTrue();
      expect(compared?.preview.panFillRatio).toEqual({ numerator: '4', denominator: '3' });
    });

    it('refuses a status it does not recognise', () => {
      expect(decodeRecipeYieldReconciliationResult(payload({ status: 'Approximated' }))).toBeNull();
    });

    it('refuses a solved field it does not recognise', () => {
      expect(decodeRecipeYieldReconciliationResult(payload({ solvedField: 'PanVolume' }))).toBeNull();
    });

    // A present-but-unreadable ratio is not the same as no ratio at all.
    it('refuses a fill ratio that is present but unreadable', () => {
      expect(
        decodeRecipeYieldReconciliationResult(payload({ panComparable: true, panFillRatio: 1.33 })),
      ).toBeNull();
    });

    it('refuses a pan-comparable flag that is not a boolean', () => {
      expect(decodeRecipeYieldReconciliationResult(payload({ panComparable: 'yes' }))).toBeNull();
    });

    it('refuses a value that is not an object', () => {
      expect(decodeRecipeYieldReconciliationResult(null)).toBeNull();
    });
  });

  describe('encodeRecalculateYieldRequest', () => {
    it('sends every value that was entered', () => {
      expect(
        encodeRecalculateYieldRequest({
          sourceVersionNumber: 3,
          batchYield: 12,
          servingCount: 6,
          servingSize: 2,
          panVolume: 9,
          displayPrecision: 2,
        }),
      ).toEqual({
        sourceVersionNumber: 3,
        batchYield: 12,
        servingCount: 6,
        servingSize: 2,
        panVolume: 9,
        displayPrecision: 2,
      });
    });

    // Omitted rather than sent as null: a request that names only what was entered is the one that matches
    // what the panel says it does.
    it('omits every value left blank', () => {
      expect(
        encodeRecalculateYieldRequest({
          sourceVersionNumber: 3,
          batchYield: null,
          servingCount: 6,
          servingSize: 2,
          panVolume: null,
          displayPrecision: null,
        }),
      ).toEqual({ sourceVersionNumber: 3, servingCount: 6, servingSize: 2 });
    });

    // Too few values is a legitimate request whose answer is a status, not something to withhold.
    it('sends a request with only one value', () => {
      expect(
        encodeRecalculateYieldRequest({
          sourceVersionNumber: 3,
          batchYield: 12,
          servingCount: null,
          servingSize: null,
          panVolume: null,
          displayPrecision: null,
        }),
      ).toEqual({ sourceVersionNumber: 3, batchYield: 12 });
    });
  });
});
