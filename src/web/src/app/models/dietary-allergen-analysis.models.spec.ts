import {
  decodeDietaryAllergenAnalysis,
  encodeDietaryAllergenSelection,
} from './dietary-allergen-analysis.models';

/** A payload shaped exactly as the route documents it (openapi-v1.json, DietaryAllergenAnalysis). */
function payload(): Record<string, unknown> {
  return {
    recipeId: 'r1',
    recipeVersionId: 'v4',
    versionNumber: 4,
    methodVersion: 'dietary-allergen/1',
    evaluatedOn: '2026-10-20',
    crossContactDisclaimer: 'This analysis reads reviewed ingredient records only.',
    allergens: [
      {
        allergenId: 'a1',
        allergenName: 'Milk',
        finding: 'Contains',
        confidence: 'Partial',
        coverage: { linesRead: 3, linesWithCountedRecords: 2 },
        statement: 'Contains Milk, according to the cited records.',
        containingLines: ['l1'],
        possibleSources: [],
        unknownLines: ['l3'],
        conflictingLines: [],
        lines: [
          {
            lineId: 'l1',
            displayText: '250 g butter',
            isOptional: false,
            ingredientId: 'i1',
            finding: 'Contains',
            reason: 'None',
            conflict: false,
            evidence: [
              {
                presence: 'Present',
                sourceCode: 'fdc',
                sourceName: 'FoodData Central',
                sourceKind: 'OfficialDatabase',
                citation: 'FDC 173410',
                effectiveFrom: '2026-01-01',
                evidenceNote: 'Butter is a milk product.',
                counted: true,
              },
            ],
          },
          {
            lineId: 'l3',
            displayText: 'a pinch of nutmeg',
            isOptional: true,
            ingredientId: null,
            finding: 'Unknown',
            reason: 'NotARecognisedIngredient',
            conflict: false,
            evidence: [],
          },
        ],
      },
    ],
    dietary: [
      {
        dietaryProfileId: 'd1',
        dietaryProfileName: 'Vegan',
        finding: 'DoesNotFit',
        confidence: 'Disputed',
        coverage: { linesRead: 3, linesWithCountedRecords: 3 },
        statement: 'Does not fit Vegan, according to the cited records.',
        incompatibleLines: ['l1'],
        dependsOnProductLines: [],
        unknownLines: [],
        conflictingLines: ['l1'],
        lines: [
          {
            lineId: 'l1',
            displayText: '250 g butter',
            isOptional: false,
            ingredientId: 'i1',
            finding: 'Incompatible',
            reason: 'None',
            conflict: true,
            evidence: [
              {
                compatibility: 'Compatible',
                sourceCode: 'forum',
                sourceName: 'Community notes',
                sourceKind: 'CommunityContributed',
                citation: 'thread 91',
                effectiveFrom: '2025-06-01',
                evidenceNote: 'Someone reported a plant-based version.',
                counted: false,
              },
            ],
          },
        ],
      },
    ],
    sources: [
      {
        code: 'fdc',
        name: 'FoodData Central',
        kind: 'OfficialDatabase',
        citation: 'USDA FoodData Central',
        licenseCode: 'CC0-1.0',
        licenseUrl: 'https://example.test/cc0',
        isActive: true,
        latestEffectiveFrom: '2026-01-01',
      },
      {
        code: 'forum',
        name: 'Community notes',
        kind: 'CommunityContributed',
        citation: 'thread 91',
        licenseCode: null,
        licenseUrl: null,
        isActive: false,
        latestEffectiveFrom: '2025-06-01',
      },
    ],
  };
}

describe('dietary-allergen-analysis.models', () => {
  it('decodes an analysis whole, keeping every finding, sentence and citation as the server wrote it', () => {
    const analysis = decodeDietaryAllergenAnalysis(payload());

    expect(analysis).not.toBeNull();
    expect(analysis?.versionNumber).toBe(4);
    expect(analysis?.methodVersion).toBe('dietary-allergen/1');
    expect(analysis?.evaluatedOn).toBe('2026-10-20');
    expect(analysis?.allergens[0].statement).toBe('Contains Milk, according to the cited records.');
    expect(analysis?.allergens[0].lines[1].displayText).toBe('a pinch of nutmeg');
    expect(analysis?.allergens[0].lines[1].ingredientId).toBeNull();
    expect(analysis?.dietary[0].lines[0].evidence[0].counted).toBeFalse();
    expect(analysis?.sources[1].licenseCode).toBeNull();
    expect(analysis?.sources[1].isActive).toBeFalse();
  });

  it('copies both id lists into the request body', () => {
    const body = encodeDietaryAllergenSelection({ allergenIds: ['a1', 'a2'], dietaryProfileIds: ['d1'] });

    expect(body).toEqual({ allergenIds: ['a1', 'a2'], dietaryProfileIds: ['d1'] });
  });

  it('sends both lists even when one is empty, so an omitted field never means the other', () => {
    expect(encodeDietaryAllergenSelection({ allergenIds: [], dietaryProfileIds: ['d1'] })).toEqual({
      allergenIds: [],
      dietaryProfileIds: ['d1'],
    });
  });

  // ---- What it refuses to read ------------------------------------------
  //
  // Each of these returns null for the *whole* analysis. A half-decoded one would drop a finding, and a
  // dropped finding reads as "nothing was found about this" — the one meaning this data may never acquire by
  // accident.

  it('refuses the analysis when a whole-recipe finding is a word it does not know', () => {
    const raw = payload();
    (raw['allergens'] as Record<string, unknown>[])[0]['finding'] = 'DefinitelyFree';

    expect(decodeDietaryAllergenAnalysis(raw)).toBeNull();
  });

  it('refuses the analysis when a line finding is a word it does not know', () => {
    const raw = payload();
    const lines = (raw['allergens'] as Record<string, unknown>[])[0]['lines'] as Record<string, unknown>[];
    lines[0]['finding'] = 'Absent';

    expect(decodeDietaryAllergenAnalysis(raw)).toBeNull();
  });

  it('refuses the analysis when a confidence is a word it does not know', () => {
    const raw = payload();
    (raw['dietary'] as Record<string, unknown>[])[0]['confidence'] = 'Certain';

    expect(decodeDietaryAllergenAnalysis(raw)).toBeNull();
  });

  it('refuses the analysis when a source kind is a word it does not know', () => {
    const raw = payload();
    (raw['sources'] as Record<string, unknown>[])[0]['kind'] = 'Hearsay';

    expect(decodeDietaryAllergenAnalysis(raw)).toBeNull();
  });

  it('refuses an analysis that arrives without the cross-contact disclaimer', () => {
    const missing = payload();
    delete missing['crossContactDisclaimer'];
    expect(decodeDietaryAllergenAnalysis(missing)).toBeNull();

    const blank = payload();
    blank['crossContactDisclaimer'] = '   ';
    expect(decodeDietaryAllergenAnalysis(blank)).toBeNull();
  });

  it('refuses a date it cannot read as a date', () => {
    const raw = payload();
    raw['evaluatedOn'] = '20 October 2026';

    expect(decodeDietaryAllergenAnalysis(raw)).toBeNull();
  });

  it('refuses coverage counts that are not whole numbers', () => {
    const raw = payload();
    (raw['allergens'] as Record<string, unknown>[])[0]['coverage'] = {
      linesRead: 3.5,
      linesWithCountedRecords: 2,
    };

    expect(decodeDietaryAllergenAnalysis(raw)).toBeNull();
  });

  it('refuses a line id list that is not a list of ids', () => {
    const raw = payload();
    (raw['allergens'] as Record<string, unknown>[])[0]['unknownLines'] = [3];

    expect(decodeDietaryAllergenAnalysis(raw)).toBeNull();
  });

  it('refuses anything that is not an object at all', () => {
    expect(decodeDietaryAllergenAnalysis(null)).toBeNull();
    expect(decodeDietaryAllergenAnalysis('ok')).toBeNull();
    expect(decodeDietaryAllergenAnalysis([])).toBeNull();
  });
});
