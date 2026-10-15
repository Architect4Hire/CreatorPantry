/** Test data for workflow runs, shared by the specs. Imported by nothing the application ships. */

/** A run as the server sends it: on its second step of three, with one link and one warning. */
export function wireRun(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'run-1',
    definitionKey: 'recipe.create',
    definitionVersion: 1,
    goal: 'Create a recipe',
    expectedOutcome: 'A saved recipe you can keep improving.',
    title: 'Soda bread',
    status: 'InProgress',
    stepCount: 3,
    completedStepCount: 1,
    skippedStepCount: 0,
    startedAt: '2026-10-15T09:00:00Z',
    updatedAt: '2026-10-15T09:30:00Z',
    completedAt: null,
    abandonedAt: null,
    abandonReason: null,
    lastRoute: null,
    replacedByRunId: null,
    concurrencyToken: 'AAAAAAAAB9E=',
    steps: [
      { key: 'start', title: 'Start your recipe', requirement: 'Required', status: 'Completed', completedAt: '2026-10-15T09:10:00Z' },
      { key: 'ingredients', title: 'Ingredients', requirement: 'Required', status: 'NotStarted' },
      { key: 'photo', title: 'Add a photo', requirement: 'Optional', status: 'NotStarted' },
    ],
    links: [{ kind: 'Recipe', resourceId: 'recipe-1', stepKey: null }],
    nextAction: {
      stepKey: 'ingredients',
      title: 'Ingredients',
      help: 'List what goes in.',
      requirement: 'Required',
      actions: [
        { capabilityKey: 'recipes.edit', name: 'Open your recipe', route: 'recipes/recipe-1', available: true, minimumRole: 'Contributor', youCanDoIt: true },
      ],
      waitingFor: [],
    },
    resumeRoute: 'recipes/recipe-1',
    readyToComplete: false,
    warnings: [{ code: 'newer_version', message: 'A newer version of this workflow is available.' }],
    ...overrides,
  };
}
