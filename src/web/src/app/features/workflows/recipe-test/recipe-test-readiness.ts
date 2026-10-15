import { RecipeReadiness } from '../../../models/recipe-readiness.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeReadinessService } from '../../../services/recipe-readiness.service';
import { linkedRecipeId } from '../recipe-steps/recipe-run';

/**
 * Whether the server's readiness check found a rule met, read as the workflow's completion rule reads it: satisfied,
 * not applicable, or switched off for this workspace. A rule the check did not report and that is not switched off
 * could not be checked, and is not met. For saying what is still needed — the server still decides the step.
 */
export function readinessRuleMet(readiness: RecipeReadiness, ruleId: string): boolean {
  const finding = readiness.findings.find((candidate) => candidate.ruleId === ruleId);
  if (finding === undefined) return readiness.disabledRuleIds.includes(ruleId);
  return finding.status === 'Satisfied' || finding.status === 'NotApplicable';
}

/** The run's recipe's readiness, or null when there is no recipe or it could not be read just now. */
export async function readRunReadiness(
  service: RecipeReadinessService,
  workspaceSlug: string,
  run: WorkflowRun,
): Promise<RecipeReadiness | null> {
  const id = linkedRecipeId(run);
  if (id === null) return null;
  const outcome = await service.getReadiness(workspaceSlug, id);
  return outcome.status === 'found' ? outcome.readiness : null;
}

/** Said when a step has no recipe to work on: the creator's words, not the workflow's. */
export const NO_RECIPE_YET = 'There’s no recipe chosen yet. Go back to the first step and choose one.';
