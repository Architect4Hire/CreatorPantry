import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';

/** The key of the "Create a recipe" journey (`WorkflowCatalogue.RecipeCreateKey`). */
export const RECIPE_CREATE_DEFINITION_KEY = 'recipe.create';

/** The key of the "Improve a recipe" journey (`WorkflowCatalogue.RecipeImproveKey`). */
export const RECIPE_IMPROVE_DEFINITION_KEY = 'recipe.improve';

/** The key of the "Test and approve" journey (`WorkflowCatalogue.RecipeTestAndApproveKey`). */
export const RECIPE_TEST_DEFINITION_KEY = 'recipe.test-and-approve';

/**
 * The recipe this run holds: the newest one linked, as the server keeps them in the order they were linked. Null
 * before the first step has made one.
 */
export function linkedRecipeId(run: WorkflowRun): string | null {
  const recipes = run.links.filter((link) => link.kind === 'Recipe');
  return recipes.length > 0 ? recipes[recipes.length - 1].id : null;
}

/** The record a step's unfinished work is kept in, or null when it has none. */
export function stepDraft(run: WorkflowRun, stepKey: string): WorkflowResourcePointer | null {
  return run.steps.find((step) => step.key === stepKey)?.draft ?? null;
}
