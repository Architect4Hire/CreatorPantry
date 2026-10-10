import { AiProposalDetail, AiProposedChange } from './ai-proposal.models';
import {
  countItems,
  draftFromProposal,
  ingredientLineStatesAmount,
  missingRequiredFieldsOf,
  otherWarningsOf,
  unresolvedQuestionsOf,
} from './recipe-draft.models';

let nextChange = 0;

function change(partial: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: `change-${nextChange++}`,
    changeKind: 'Set',
    targetKind: 'Recipe',
    targetId: null,
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...partial,
  };
}

function proposal(changes: readonly AiProposedChange[], warnings: AiProposalDetail['warnings'] = []): AiProposalDetail {
  return {
    proposalId: 'proposal-1',
    outputSchemaVersion: 'recipe.first-draft.v1',
    promptTemplateId: 'recipe.first-draft',
    promptTemplateVersion: '1.0.0',
    promptTemplateBodyChecksum: 'sha256:abc',
    providerName: 'test-provider',
    modelName: 'test-model',
    createdAt: '2026-09-28T12:00:00Z',
    changes,
    warnings,
  };
}

/** Recipe-level `Set`: no target id, by the handler's own convention. */
function recipeField(fieldName: string, afterValue: string): AiProposedChange {
  return change({ changeKind: 'Set', targetKind: 'Recipe', targetId: null, fieldName, afterValue });
}

describe('draftFromProposal', () => {
  it('returns an empty draft for no proposal', () => {
    const draft = draftFromProposal(null);

    expect(draft.title).toBeNull();
    expect(draft.ingredientGroups).toEqual([]);
    expect(draft.instructionGroups).toEqual([]);
    expect(draft.unresolvedQuestions).toEqual([]);
  });

  it('reads the recipe-level fields', () => {
    const draft = draftFromProposal(
      proposal([
        recipeField('title', 'Weeknight Mapo Tofu'),
        recipeField('description', 'A quick version.'),
        recipeField('notes', 'Best served immediately.'),
        recipeField('prepTimeMinutes', '10'),
        recipeField('cookTimeMinutes', '15'),
        recipeField('yieldText', 'Serves 4'),
        recipeField('servingCount', '4'),
      ]),
    );

    expect(draft.title).toBe('Weeknight Mapo Tofu');
    expect(draft.description).toBe('A quick version.');
    expect(draft.notes).toBe('Best served immediately.');
    expect(draft.timing.prepTimeMinutes).toBe('10');
    expect(draft.timing.cookTimeMinutes).toBe('15');
    expect(draft.yield.yieldText).toBe('Serves 4');
    expect(draft.yield.servingCount).toBe('4');
  });

  it('leaves a field the model did not propose as null rather than blank', () => {
    const draft = draftFromProposal(proposal([recipeField('title', 'Soup')]));

    expect(draft.timing.restTimeMinutes).toBeNull();
    expect(draft.timing.totalTimeMinutes).toBeNull();
    expect(draft.yield.yieldUnitText).toBeNull();
  });

  /**
   * The convention the handler's own remarks describe, and the reason this walks the array forward: nothing
   * exists yet on either end of a group/line relationship, so a line does not name its group — it follows it.
   */
  it('assigns each line to the group that precedes it in the list', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: 'For the sauce', proposedPosition: 0 }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: '2 tbsp doubanjiang', proposedPosition: 0 }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l2', afterValue: 'Salt, to taste', proposedPosition: 1 }),
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g2', afterValue: 'For the tofu', proposedPosition: 1 }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l3', afterValue: '1 block silken tofu', proposedPosition: 0 }),
      ]),
    );

    expect(draft.ingredientGroups.length).toBe(2);
    expect(draft.ingredientGroups[0].title).toBe('For the sauce');
    expect(draft.ingredientGroups[0].items.map((line) => line.displayText)).toEqual([
      '2 tbsp doubanjiang',
      'Salt, to taste',
    ]);
    expect(draft.ingredientGroups[1].title).toBe('For the tofu');
    expect(draft.ingredientGroups[1].items.map((line) => line.displayText)).toEqual(['1 block silken tofu']);
  });

  it('places a lines own Set rows onto that line', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: null }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: '2 tbsp doubanjiang' }),
        change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'ingredientNameText', afterValue: 'doubanjiang' }),
        change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'unitText', afterValue: 'tbsp' }),
        change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'quantity', afterValue: '2' }),
        change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'preparationNote', afterValue: 'finely chopped' }),
      ]),
    );

    const line = draft.ingredientGroups[0].items[0];
    expect(line.ingredientNameText).toBe('doubanjiang');
    expect(line.unitText).toBe('tbsp');
    expect(line.quantity).toBe('2');
    expect(line.preparationNote).toBe('finely chopped');
    expect(line.isOptional).toBe(false);
  });

  it('marks a line optional when the handler emitted the flag', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: null }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: 'Salt, to taste' }),
        change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'isOptional', afterValue: 'true' }),
      ]),
    );

    expect(draft.ingredientGroups[0].items[0].isOptional).toBe(true);
  });

  it('marks a whole group optional', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: 'For the streusel' }),
        change({ changeKind: 'Set', targetKind: 'IngredientGroup', targetId: 'g1', fieldName: 'isOptional', afterValue: 'true' }),
      ]),
    );

    expect(draft.ingredientGroups[0].isOptional).toBe(true);
  });

  /** recipes.md makes grouping opt-in: an untitled group is the ordinary case, not a missing heading. */
  it('keeps an untitled group untitled rather than inventing a heading', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: null }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: 'Flour' }),
      ]),
    );

    expect(draft.ingredientGroups[0].title).toBeNull();
  });

  it('treats a blank group title as untitled', () => {
    const draft = draftFromProposal(
      proposal([change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: '' })]),
    );

    expect(draft.ingredientGroups[0].title).toBeNull();
  });

  it('reads instruction groups and their steps', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'InstructionGroup', targetId: 'ig1', afterValue: 'Make the sauce' }),
        change({ changeKind: 'Add', targetKind: 'InstructionStep', targetId: 's1', afterValue: 'Fry the paste.' }),
        change({ changeKind: 'Set', targetKind: 'InstructionStep', targetId: 's1', fieldName: 'note', afterValue: 'Do not burn it.' }),
        change({ changeKind: 'Set', targetKind: 'InstructionStep', targetId: 's1', fieldName: 'durationMinutes', afterValue: '2' }),
        change({ changeKind: 'Add', targetKind: 'InstructionStep', targetId: 's2', afterValue: 'Add stock.' }),
      ]),
    );

    expect(draft.instructionGroups.length).toBe(1);
    expect(draft.instructionGroups[0].title).toBe('Make the sauce');
    const [first, second] = draft.instructionGroups[0].items;
    expect(first.text).toBe('Fry the paste.');
    expect(first.note).toBe('Do not burn it.');
    expect(first.durationMinutes).toBe('2');
    expect(second.text).toBe('Add stock.');
  });

  it('reads equipment with notes and optional flags', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'Equipment', targetId: 'e1', afterValue: 'Wok' }),
        change({ changeKind: 'Set', targetKind: 'Equipment', targetId: 'e1', fieldName: 'note', afterValue: 'A wide frying pan works.' }),
        change({ changeKind: 'Add', targetKind: 'Equipment', targetId: 'e2', afterValue: 'Rice cooker' }),
        change({ changeKind: 'Set', targetKind: 'Equipment', targetId: 'e2', fieldName: 'isOptional', afterValue: 'true' }),
      ]),
    );

    expect(draft.equipment.length).toBe(2);
    expect(draft.equipment[0].note).toBe('A wide frying pan works.');
    expect(draft.equipment[0].isOptional).toBe(false);
    expect(draft.equipment[1].isOptional).toBe(true);
  });

  /**
   * An ingredient line and an equipment item both carry `displayText`, and only one of them has a `note`.
   * A `note` addressed to an ingredient is reported rather than silently stuck on.
   */
  it('does not place a field belonging to a different kind of row', () => {
    const stray = change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'note', afterValue: 'nope' });
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: null }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: 'Flour' }),
        stray,
      ]),
    );

    expect(draft.unrecognisedFields).toContain(stray);
    expect(draft.ingredientGroups[0].items[0]).not.toEqual(jasmine.objectContaining({ note: 'nope' }));
  });

  /** A server-sent field name must not be able to overwrite this view's own identity fields. */
  it('refuses a field name that collides with the view models own keys', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: 'Real heading' }),
        change({ changeKind: 'Set', targetKind: 'IngredientGroup', targetId: 'g1', fieldName: 'title', afterValue: 'hijacked' }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: 'Flour' }),
        change({ changeKind: 'Set', targetKind: 'Ingredient', targetId: 'l1', fieldName: 'changeId', afterValue: 'hijacked' }),
      ]),
    );

    expect(draft.ingredientGroups[0].title).toBe('Real heading');
    expect(draft.ingredientGroups[0].items[0].changeId).not.toBe('hijacked');
    expect(draft.unrecognisedFields.length).toBe(2);
  });

  /** Losing a proposed ingredient because the ordering surprised us would be worse than showing it ungrouped. */
  it('keeps a line that arrives before any group, in an untitled group of its own', () => {
    const draft = draftFromProposal(
      proposal([change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: 'Orphan line' })]),
    );

    expect(draft.ingredientGroups.length).toBe(1);
    expect(draft.ingredientGroups[0].title).toBeNull();
    expect(draft.ingredientGroups[0].items[0].displayText).toBe('Orphan line');
  });

  it('reports a recipe-level field it does not know rather than dropping it', () => {
    const unknown = recipeField('somethingNew', 'a value');
    const draft = draftFromProposal(proposal([unknown]));

    expect(draft.unrecognisedFields).toContain(unknown);
  });
});

describe('warnings', () => {
  const questions = proposal(
    [],
    [
      { kind: 'UnresolvedQuestion', message: 'What chili oil brand?', changeId: null },
      { kind: 'Assumption', message: 'Assumed a standard wok.', changeId: null },
      { kind: 'SafetyCaution', message: 'Check the internal temperature yourself.', changeId: null },
    ],
  );

  it('separates unresolved questions from the other flags', () => {
    expect(unresolvedQuestionsOf(questions).map((warning) => warning.message)).toEqual(['What chili oil brand?']);
    expect(otherWarningsOf(questions).map((warning) => warning.kind)).toEqual(['Assumption', 'SafetyCaution']);
  });

  it('puts them on the draft under their own keys', () => {
    const draft = draftFromProposal(questions);

    expect(draft.unresolvedQuestions.length).toBe(1);
    expect(draft.warnings.length).toBe(2);
  });

  it('handles no proposal', () => {
    expect(unresolvedQuestionsOf(null)).toEqual([]);
    expect(otherWarningsOf(null)).toEqual([]);
  });
});

describe('missingRequiredFieldsOf', () => {
  it('names everything a recipe cannot be created without', () => {
    const draft = draftFromProposal(proposal([]));

    expect(missingRequiredFieldsOf(draft)).toEqual(['Title', 'Ingredients', 'Method']);
  });

  it('treats a whitespace-only title as missing', () => {
    const draft = draftFromProposal(proposal([recipeField('title', '   ')]));

    expect(missingRequiredFieldsOf(draft)).toContain('Title');
  });

  it('is empty for a complete draft', () => {
    const draft = draftFromProposal(
      proposal([
        recipeField('title', 'Soup'),
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: null }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: 'Water' }),
        change({ changeKind: 'Add', targetKind: 'InstructionGroup', targetId: 'ig1', afterValue: null }),
        change({ changeKind: 'Add', targetKind: 'InstructionStep', targetId: 's1', afterValue: 'Boil it.' }),
      ]),
    );

    expect(missingRequiredFieldsOf(draft)).toEqual([]);
  });

  /** A group with no lines in it is still no ingredients. */
  it('counts an empty group as no ingredients', () => {
    const draft = draftFromProposal(
      proposal([
        recipeField('title', 'Soup'),
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: 'Empty' }),
      ]),
    );

    expect(missingRequiredFieldsOf(draft)).toContain('Ingredients');
  });
});

describe('countItems', () => {
  it('totals across groups', () => {
    const draft = draftFromProposal(
      proposal([
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1', afterValue: 'A' }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l1', afterValue: 'One' }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l2', afterValue: 'Two' }),
        change({ changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g2', afterValue: 'B' }),
        change({ changeKind: 'Add', targetKind: 'Ingredient', targetId: 'l3', afterValue: 'Three' }),
      ]),
    );

    expect(countItems(draft.ingredientGroups)).toBe(3);
  });
});

describe('ingredientLineStatesAmount', () => {
  it('accepts a line that states a number, however it is written', () => {
    expect(ingredientLineStatesAmount('250 g plain flour, sifted')).toBeTrue();
    expect(ingredientLineStatesAmount('1 1/2 cups stone-ground cornmeal')).toBeTrue();
    expect(ingredientLineStatesAmount('1½ cups buttermilk')).toBeTrue();
    expect(ingredientLineStatesAmount('2 x 400 g tins chopped tomatoes')).toBeTrue();
  });

  /** The prompt names these as complete lines, so flagging one would be crying wolf. */
  it('accepts a measure that is not a number', () => {
    expect(ingredientLineStatesAmount('a pinch of cayenne')).toBeTrue();
    expect(ingredientLineStatesAmount('Flaky salt, to taste')).toBeTrue();
    expect(ingredientLineStatesAmount('a handful of parsley, chopped')).toBeTrue();
    expect(ingredientLineStatesAmount('Butter, for greasing')).toBeTrue();
  });

  it('rejects a line that only names an ingredient', () => {
    expect(ingredientLineStatesAmount('plain flour, sifted')).toBeFalse();
    expect(ingredientLineStatesAmount('buttermilk')).toBeFalse();
    expect(ingredientLineStatesAmount('unsalted butter, softened')).toBeFalse();
  });
});
