/**
 * The read model as the route sends it (13.11, 13A.33a), for the dashboard's specs. Every section is ready and
 * has a little in it; a spec changes what it is about.
 */
export function dashboardBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const ready = (data: unknown): Record<string, unknown> => ({ status: 'Ready', data });

  return {
    generatedAt: '2026-10-14T15:30:00+00:00',
    timeZoneId: 'America/Chicago',
    today: '2026-10-14',
    timeZoneState: 'Set',
    workflows: ready({
      inProgressCount: 4,
      continue: [
        {
          id: 'w1', title: 'Soda bread, start to finish', definitionKey: 'recipe.create', goal: 'Create a recipe',
          stepKey: 'method', stepTitle: 'Check your method', youCanDoIt: true,
          updatedAt: '2026-10-14T11:00:00+00:00', attentionReasons: [],
        },
        {
          id: 'w2', title: 'Granola, improved', definitionKey: 'recipe.improve', goal: 'Improve a recipe',
          stepKey: 'approve', stepTitle: 'Approve it', youCanDoIt: false,
          updatedAt: '2026-10-13T11:00:00+00:00', attentionReasons: ['not_yours_to_do'],
        },
      ],
      needsAttentionCount: 2,
      needsAttention: [
        {
          id: 'w3', title: 'Autumn shoot', definitionKey: 'images.create', goal: 'Make pictures',
          stepKey: 'brief', stepTitle: 'Write the brief', youCanDoIt: true,
          updatedAt: '2026-09-20T11:00:00+00:00', attentionReasons: ['idle', 'newer_version'],
        },
      ],
    }),
    workPlan: ready({
      date: '2026-10-14',
      timeZoneId: 'America/Chicago',
      overdueCount: 2,
      overdue: [
        { id: 'k1', title: 'Answer the brand email', status: 'Backlog', priority: 'High', dueDate: '2026-10-12', scheduledDate: null },
      ],
      todayCount: 3,
      today: [
        { id: 'k2', title: 'Shoot the galette', status: 'Backlog', priority: 'Normal', dueDate: null, scheduledDate: '2026-10-14' },
        { id: 'k3', title: 'Write the headnote', status: 'Backlog', priority: 'Low', dueDate: '2026-10-16', scheduledDate: '2026-10-14' },
      ],
      topThree: ['k2'],
      thisWeek: {
        weekStart: '2026-10-12',
        weekEnd: '2026-10-18',
        plannedTaskCount: 6,
        estimatedTaskCount: 4,
        estimatedMinutes: 150,
        inProgressCount: 1,
        waitingCount: 1,
        completedCount: 2,
      },
      unplannedCount: 5,
    }),
    recipesInProgress: ready({
      counts: [
        { status: 'Draft', count: 3 },
        { status: 'InDevelopment', count: 0 },
        { status: 'Testing', count: 2 },
        { status: 'ReadyForReview', count: 1 },
      ],
      recent: [{ id: 'r1', title: 'Soda bread', status: 'Testing', updatedAt: '2026-10-14T09:00:00+00:00' }],
    }),
    readinessBlockers: ready({
      evaluated: 1,
      unevaluated: 0,
      blocked: [
        {
          recipeId: 'r1',
          title: 'Soda bread',
          blockerCount: 4,
          blockers: [
            { ruleId: 'ingredients.present', summary: 'Add at least one ingredient.' },
            { ruleId: 'method.present', summary: 'Add at least one step.' },
            { ruleId: 'yield.stated', summary: 'Say how much it makes.' },
          ],
        },
      ],
    }),
    aiProposals: ready({
      awaitingDecision: 7,
      recent: [
        { operationId: 'op1', taskType: 'RecipeRevision', recipeId: 'r1', recipeTitle: 'Soda bread', proposedAt: '2026-10-14T10:00:00+00:00' },
        { operationId: 'op2', taskType: 'RecipeConcepts', recipeId: null, recipeTitle: null, proposedAt: '2026-10-14T08:00:00+00:00' },
      ],
    }),
    testFollowUps: ready({
      open: 6,
      blocking: 1,
      major: 2,
      minor: 3,
      recent: [
        {
          issueId: 'i1', testRunId: 't1', recipeId: 'r1', recipeTitle: 'Soda bread', title: 'Crust burns at 220°C',
          severity: 'Blocking', testedAt: '2026-10-13T18:00:00+00:00',
        },
      ],
    }),
    upcoming: ready({
      from: '2026-10-14',
      through: '2026-10-27',
      cardCount: 9,
      cards: [{ id: 'c1', title: 'Soda bread, Friday', column: 'InProcess', plannedDate: '2026-10-16', channelKey: 'instagram' }],
      postCount: 2,
      posts: [
        { id: 'p1', contentCardId: 'c1', contentCardTitle: 'Soda bread, Friday', platform: 'Instagram', status: 'Scheduled', scheduledAt: '2026-10-16T14:00:00+00:00' },
        { id: 'p2', contentCardId: null, contentCardTitle: null, platform: 'Pinterest', status: 'Pending', scheduledAt: '2026-10-17T14:00:00+00:00' },
      ],
    }),
    board: ready({
      total: 12,
      columns: [
        { column: 'ToDo', count: 5 },
        { column: 'InProcess', count: 4 },
        { column: 'ReadyForReview', count: 2 },
        { column: 'Approved', count: 1 },
      ],
    }),
    recentUploads: ready({ items: [{ id: 'a1', title: 'Soda bread hero', kind: 'Original', createdAt: '2026-10-14T07:00:00+00:00' }] }),
    publishing: ready({
      counts: [
        { status: 'Pending', count: 1 }, { status: 'Submitting', count: 0 }, { status: 'Scheduled', count: 1 },
        { status: 'Published', count: 8 }, { status: 'Cancelling', count: 0 }, { status: 'Cancelled', count: 0 },
        { status: 'Failed', count: 2 }, { status: 'NeedsAttention', count: 1 },
      ],
      needsAttentionCount: 3,
      needsAttention: [
        { id: 'p3', contentCardId: 'c2', contentCardTitle: 'Granola', platform: 'Instagram', status: 'Failed', scheduledAt: '2026-10-12T14:00:00+00:00' },
        { id: 'p4', contentCardId: null, contentCardTitle: null, platform: 'Other', status: 'NeedsAttention', scheduledAt: '2026-10-11T14:00:00+00:00' },
      ],
    }),
    ...overrides,
  };
}
