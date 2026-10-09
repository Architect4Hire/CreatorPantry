import { AiProposalDetail, AiProposedChange, AiProposalWarning } from './ai-proposal.models';
import {
  EMPTY_RECIPE_CONCEPTS_REQUEST,
  conceptsFromProposal,
  encodeRequestRecipeConceptsRequest,
  generalConceptWarnings,
} from './recipe-concept.models';

function addRow(overrides: Partial<AiProposedChange> = {}): AiProposedChange {
  return {
    changeId: 'add-1',
    changeKind: 'Add',
    targetKind: 'RecipeConcept',
    targetId: 'concept-1',
    fieldName: null,
    beforeValue: null,
    afterValue: 'Slow-roasted tomato soup',
    proposedPosition: 0,
    disposition: 'Pending',
    ...overrides,
  };
}

function setRow(field: string, value: string, overrides: Partial<AiProposedChange> = {}): AiProposedChange {
  return {
    changeId: `set-${field}`,
    changeKind: 'Set',
    targetKind: 'RecipeConcept',
    targetId: 'concept-1',
    fieldName: field,
    beforeValue: null,
    afterValue: value,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function detail(changes: readonly AiProposedChange[], warnings: readonly AiProposalWarning[] = []): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: 'recipe.concepts.v1',
    promptTemplateId: 'recipe.concepts',
    promptTemplateVersion: '1.0.0',
    promptTemplateBodyChecksum: 'sha256:abc',
    providerName: 'foundry-local',
    modelName: 'phi-4',
    createdAt: '2026-03-12T14:03:00Z',
    changes,
    warnings,
  };
}

describe('recipe-concept.models', () => {
  describe('conceptsFromProposal', () => {
    it('returns nothing for a null proposal', () => {
      expect(conceptsFromProposal(null)).toEqual([]);
    });

    it('groups an Add row with its Set rows into one concept', () => {
      const concepts = conceptsFromProposal(
        detail([
          addRow(),
          setRow('summary', 'A slow weekend soup.'),
          setRow('distinctnessRationale', 'Leans on roasting rather than simmering.'),
          setRow('assumptions', 'Using canned tomatoes; Serves four'),
          setRow('suggestedIngredients', 'Tomatoes; Garlic; Olive oil'),
          setRow('dietaryNotes', 'Reads as vegetarian'),
          setRow('timeBudgetNote', 'Fits a lazy Sunday, not a weeknight.'),
          setRow('skillLevelFit', 'Comfortable for a beginner.'),
        ]),
      );

      expect(concepts.length).toBe(1);
      const concept = concepts[0];
      expect(concept.targetId).toBe('concept-1');
      expect(concept.changeId).toBe('add-1');
      expect(concept.title).toBe('Slow-roasted tomato soup');
      expect(concept.summary).toBe('A slow weekend soup.');
      expect(concept.distinctnessRationale).toBe('Leans on roasting rather than simmering.');
      expect(concept.assumptions).toEqual(['Using canned tomatoes', 'Serves four']);
      expect(concept.suggestedIngredients).toEqual(['Tomatoes', 'Garlic', 'Olive oil']);
      expect(concept.dietaryNotes).toEqual(['Reads as vegetarian']);
      expect(concept.timeBudgetNote).toBe('Fits a lazy Sunday, not a weeknight.');
      expect(concept.skillLevelFit).toBe('Comfortable for a beginner.');
    });

    it('leaves list fields empty and note fields null when their Set row is absent', () => {
      const concepts = conceptsFromProposal(detail([addRow()]));

      expect(concepts[0].assumptions).toEqual([]);
      expect(concepts[0].suggestedIngredients).toEqual([]);
      expect(concepts[0].dietaryNotes).toEqual([]);
      expect(concepts[0].timeBudgetNote).toBeNull();
      expect(concepts[0].skillLevelFit).toBeNull();
      expect(concepts[0].summary).toBeNull();
    });

    it('orders concepts by proposedPosition regardless of row order in the array', () => {
      const concepts = conceptsFromProposal(
        detail([
          addRow({ changeId: 'add-2', targetId: 'concept-2', afterValue: 'Second concept', proposedPosition: 1 }),
          addRow({ changeId: 'add-1', targetId: 'concept-1', afterValue: 'First concept', proposedPosition: 0 }),
        ]),
      );

      expect(concepts.map((concept) => concept.title)).toEqual(['First concept', 'Second concept']);
    });

    it('keeps each concept to its own targetId, never mixing Set rows across concepts', () => {
      const concepts = conceptsFromProposal(
        detail([
          addRow({ changeId: 'add-1', targetId: 'concept-1', afterValue: 'First concept', proposedPosition: 0 }),
          addRow({ changeId: 'add-2', targetId: 'concept-2', afterValue: 'Second concept', proposedPosition: 1 }),
          setRow('summary', 'Summary for the first', { changeId: 's1', targetId: 'concept-1' }),
          setRow('summary', 'Summary for the second', { changeId: 's2', targetId: 'concept-2' }),
        ]),
      );

      expect(concepts[0].summary).toBe('Summary for the first');
      expect(concepts[1].summary).toBe('Summary for the second');
    });

    it('attaches a warning to the concept whose Add row it names', () => {
      const warning: AiProposalWarning = { kind: 'Assumption', message: 'Assumed weeknight cooking.', changeId: 'add-1' };
      const concepts = conceptsFromProposal(detail([addRow()], [warning]));

      expect(concepts[0].warnings).toEqual([warning]);
    });

    it('ignores an unrecognised Set field rather than failing the whole concept', () => {
      const concepts = conceptsFromProposal(
        detail([addRow(), setRow('somethingUnexpected', 'ignored'), setRow('summary', 'Kept')]),
      );

      expect(concepts[0].summary).toBe('Kept');
    });
  });

  describe('generalConceptWarnings', () => {
    it('returns only warnings about the answer as a whole', () => {
      const whole: AiProposalWarning = { kind: 'Assumption', message: 'The brief was contradictory.', changeId: null };
      const scoped: AiProposalWarning = { kind: 'Assumption', message: 'About one concept.', changeId: 'add-1' };

      expect(generalConceptWarnings(detail([addRow()], [whole, scoped]))).toEqual([whole]);
    });

    it('returns nothing for a null proposal', () => {
      expect(generalConceptWarnings(null)).toEqual([]);
    });
  });

  describe('encodeRequestRecipeConceptsRequest', () => {
    it('omits blank and null fields', () => {
      const body = encodeRequestRecipeConceptsRequest({
        dishName: null,
        audience: 'Weeknight home cooks',
        course: '',
        cuisine: null,
        dietaryGoals: null,
        availableIngredients: null,
        exclusions: null,
        equipment: null,
        skill: '  ',
        season: null,
        timeBudget: null,
        creatorStyle: null,
      });

      expect(body).toEqual({ audience: 'Weeknight home cooks' });
    });

    it('trims a supplied value', () => {
      const body = encodeRequestRecipeConceptsRequest({
        dishName: '  Fattoush salad with radishes  ',
        audience: '  Weeknight home cooks  ',
        course: null,
        cuisine: null,
        dietaryGoals: null,
        availableIngredients: null,
        exclusions: null,
        equipment: null,
        skill: null,
        season: null,
        timeBudget: null,
        creatorStyle: null,
      });

      expect(body['audience']).toBe('Weeknight home cooks');
      expect(body['dishName']).toBe('Fattoush salad with radishes');
    });

    it('leads the brief with the dish name, which is the subject the rest of it describes', () => {
      const body = encodeRequestRecipeConceptsRequest({
        ...EMPTY_RECIPE_CONCEPTS_REQUEST,
        dishName: 'Fattoush salad with radishes and grilled chicken shawarma',
      });

      expect(body).toEqual({ dishName: 'Fattoush salad with radishes and grilled chicken shawarma' });
    });
  });
});
