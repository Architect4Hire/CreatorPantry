import {
  IngredientLineTokens,
  ParseIngredientLinesResponse,
  ParsedIngredientLine,
  decodeParseIngredientLinesResponse,
  encodeParseIngredientLinesRequest,
} from './ingredient-parse.models';

const VALID_TOKENS: IngredientLineTokens = {
  originalText: '2 tsp kosher salt',
  isGroupMarker: false,
  quantity: {
    span: { start: 0, length: 1, text: '2' },
    value: { numerator: '2', denominator: '1' },
    range: null,
    isInvalid: false,
  },
  packageQuantity: null,
  unitCandidate: { start: 2, length: 3, text: 'tsp' },
  ingredientText: { start: 6, length: 11, text: 'kosher salt' },
  preparationText: [],
  isOptional: false,
  ambiguities: [],
};

const VALID_LINE: ParsedIngredientLine = {
  tokens: VALID_TOKENS,
  ingredientMatch: {
    inputText: 'kosher salt',
    resolved: { ingredientId: 'ing1', canonicalName: 'kosher salt', kind: 'CanonicalName' },
    alternates: [],
    isAmbiguous: false,
  },
  unitMatch: {
    inputText: 'tsp',
    resolved: { measurementUnitId: 'unit1', displayName: 'teaspoon', kind: 'Code' },
    alternates: [],
    isAmbiguous: false,
  },
};

const VALID_RESPONSE: ParseIngredientLinesResponse = { lines: [VALID_LINE] };

describe('ingredient-parse.models', () => {
  it('decodes a full valid response, including nested quantity/range and enums as PascalCase strings', () => {
    expect(decodeParseIngredientLinesResponse(VALID_RESPONSE)).toEqual(VALID_RESPONSE);
  });

  it('decodes a group-marker line, whose tokens carry null quantity/ingredientText/unitCandidate', () => {
    const groupMarkerLine: ParsedIngredientLine = {
      tokens: {
        originalText: 'For the crust:',
        isGroupMarker: true,
        quantity: null,
        packageQuantity: null,
        unitCandidate: null,
        ingredientText: null,
        preparationText: [],
        isOptional: false,
        ambiguities: [],
      },
      ingredientMatch: null,
      unitMatch: null,
    };

    expect(decodeParseIngredientLinesResponse({ lines: [groupMarkerLine] })).toEqual({ lines: [groupMarkerLine] });
  });

  it('decodes an unresolved/ambiguous match with alternates, never dropping them', () => {
    const ambiguousLine: ParsedIngredientLine = {
      ...VALID_LINE,
      ingredientMatch: {
        inputText: 'cilantro',
        resolved: null,
        alternates: [
          { ingredientId: 'ing2', canonicalName: 'Cilantro', kind: 'CanonicalName' },
          { ingredientId: 'ing3', canonicalName: 'Coriander', kind: 'Alias' },
        ],
        isAmbiguous: true,
      },
    };

    expect(decodeParseIngredientLinesResponse({ lines: [ambiguousLine] })).toEqual({ lines: [ambiguousLine] });
  });

  it('decodes a quantity range and an invalid quantity', () => {
    const rangeLine: ParsedIngredientLine = {
      ...VALID_LINE,
      tokens: {
        ...VALID_TOKENS,
        quantity: {
          span: { start: 0, length: 3, text: '1-2' },
          value: null,
          range: {
            lower: { numerator: '1', denominator: '1' },
            upper: { numerator: '2', denominator: '1' },
          },
          isInvalid: false,
        },
      },
    };
    const invalidLine: ParsedIngredientLine = {
      ...VALID_LINE,
      tokens: {
        ...VALID_TOKENS,
        quantity: { span: { start: 0, length: 3, text: '1/0' }, value: null, range: null, isInvalid: true },
        ambiguities: [{ kind: 'InvalidQuantity', span: { start: 0, length: 3, text: '1/0' }, message: 'not valid' }],
      },
    };

    expect(decodeParseIngredientLinesResponse({ lines: [rangeLine] })).toEqual({ lines: [rangeLine] });
    expect(decodeParseIngredientLinesResponse({ lines: [invalidLine] })).toEqual({ lines: [invalidLine] });
  });

  it('decodes a package quantity', () => {
    const packageLine: ParsedIngredientLine = {
      ...VALID_LINE,
      tokens: {
        ...VALID_TOKENS,
        packageQuantity: {
          span: { start: 2, length: 9, text: '(14.5 oz)' },
          quantity: {
            span: { start: 3, length: 4, text: '14.5' },
            value: { numerator: '29', denominator: '2' },
            range: null,
            isInvalid: false,
          },
          unit: { start: 8, length: 2, text: 'oz' },
        },
      },
    };

    expect(decodeParseIngredientLinesResponse({ lines: [packageLine] })).toEqual({ lines: [packageLine] });
  });

  it('rejects a non-object payload', () => {
    expect(decodeParseIngredientLinesResponse(null)).toBeNull();
    expect(decodeParseIngredientLinesResponse('not an object')).toBeNull();
  });

  it('rejects a response whose lines field is missing or not an array', () => {
    expect(decodeParseIngredientLinesResponse({})).toBeNull();
    expect(decodeParseIngredientLinesResponse({ lines: 'nope' })).toBeNull();
  });

  it('rejects an unknown ambiguity kind', () => {
    const malformed = {
      lines: [{ ...VALID_LINE, tokens: { ...VALID_TOKENS, ambiguities: [{ kind: 'NotARealKind', span: VALID_TOKENS.quantity!.span, message: 'x' }] } }],
    };

    expect(decodeParseIngredientLinesResponse(malformed)).toBeNull();
  });

  it('rejects an unknown ingredient match kind', () => {
    const malformed = {
      lines: [{ ...VALID_LINE, ingredientMatch: { ...VALID_LINE.ingredientMatch, resolved: { ingredientId: 'x', canonicalName: 'x', kind: 'NotReal' } } }],
    };

    expect(decodeParseIngredientLinesResponse(malformed)).toBeNull();
  });

  it('rejects an unknown unit match kind', () => {
    const malformed = {
      lines: [{ ...VALID_LINE, unitMatch: { ...VALID_LINE.unitMatch, resolved: { measurementUnitId: 'x', displayName: 'x', kind: 'NotReal' } } }],
    };

    expect(decodeParseIngredientLinesResponse(malformed)).toBeNull();
  });

  it('rejects a quantity token missing its span', () => {
    const malformed = { lines: [{ ...VALID_LINE, tokens: { ...VALID_TOKENS, quantity: { value: null, range: null, isInvalid: false } } }] };

    expect(decodeParseIngredientLinesResponse(malformed)).toBeNull();
  });

  it('rejects a line missing its tokens', () => {
    expect(decodeParseIngredientLinesResponse({ lines: [{ ingredientMatch: null, unitMatch: null }] })).toBeNull();
  });

  it('rejects a preparationText entry that is not a span', () => {
    const malformed = { lines: [{ ...VALID_LINE, tokens: { ...VALID_TOKENS, preparationText: ['sifted'] } }] };

    expect(decodeParseIngredientLinesResponse(malformed)).toBeNull();
  });

  it('encodes the request as a plain { lines } body', () => {
    expect(encodeParseIngredientLinesRequest({ lines: ['2 cups flour', '1 tsp salt'] })).toEqual({
      lines: ['2 cups flour', '1 tsp salt'],
    });
  });
});
