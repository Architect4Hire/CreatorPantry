import { AiProposalDetail, AiProposedChange } from './ai-proposal.models';
import { decodeDishFacetsReading, encodeRequestDishFacets } from './dish-facet.models';

function set(fieldName: string, afterValue: string): AiProposedChange {
  return {
    changeId: `c-${fieldName}`,
    changeKind: 'Set',
    targetKind: 'DishFacetSuggestion',
    targetId: 't1',
    fieldName,
    beforeValue: null,
    afterValue,
    proposedPosition: null,
    disposition: 'Pending',
  };
}

function detail(changes: readonly AiProposedChange[], warnings: AiProposalDetail['warnings'] = []): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: '1',
    promptTemplateId: 'recipe.dish-facets',
    promptTemplateVersion: '1.0.0',
    promptTemplateBodyChecksum: 'abc',
    providerName: 'provider',
    modelName: 'model',
    createdAt: '2026-10-10T12:00:00Z',
    changes,
    warnings,
  };
}

describe('dish facet models', () => {
  it('reads each facet back out of its flat rows', () => {
    const reading = decodeDishFacetsReading(
      detail([
        { ...set('', 'Fattoush Salad with Chicken Shawarma'), changeKind: 'Add', fieldName: null },
        set('facet.Cuisine', 'levantine'),
        set('facet.Cuisine.confidence', 'Likely'),
        set('facet.Cuisine.rationale', 'Fattoush and shawarma are Levantine dishes.'),
        set('facet.DishType', 'salad'),
        set('facet.DishType.confidence', 'Possible'),
        set('facet.DishType.rationale', 'The name says salad.'),
      ]),
    );

    expect(reading?.facets.cuisine).toEqual({
      code: 'levantine',
      confidence: 'Likely',
      rationale: 'Fattoush and shawarma are Levantine dishes.',
    });
    expect(reading?.facets.dishType?.confidence).toBe('Possible');
    expect(reading?.facets.method).toBeUndefined();
  });

  it('keeps a declined facet as a reason with no code', () => {
    const reading = decodeDishFacetsReading(
      detail([set('facet.Method.rationale', 'The name does not say how the salad is prepared.')]),
    );

    expect(reading?.facets.method).toEqual({
      code: null,
      confidence: null,
      rationale: 'The name does not say how the salad is prepared.',
    });
  });

  it('ignores rows that belong to another kind of proposal', () => {
    const reading = decodeDishFacetsReading(
      detail([{ ...set('facet.Cuisine', 'thai'), targetKind: 'PhotographyConcept' }]),
    );

    expect(reading?.facets).toEqual({});
  });

  it('has no reading until there is a proposal', () => {
    expect(decodeDishFacetsReading(null)).toBeNull();
  });

  it('sends the name and nothing else', () => {
    expect(encodeRequestDishFacets('  Fattoush Salad  ')).toEqual({ dishName: 'Fattoush Salad' });
  });
});
