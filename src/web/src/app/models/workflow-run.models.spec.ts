import { decodeIncompleteSteps, decodeMissing, decodeWorkflowRun, stepperStatusOf } from './workflow-run.models';
import { wireRun } from './workflow-run.testing';

describe('workflow-run models', () => {
  it('decodes a run as the server sends it', () => {
    const run = decodeWorkflowRun(wireRun());

    expect(run).not.toBeNull();
    expect(run!.goal).toBe('Create a recipe');
    expect(run!.expectedOutcome).toBe('A saved recipe you can keep improving.');
    expect(run!.steps.map((step) => step.status)).toEqual(['Completed', 'NotStarted', 'NotStarted']);
    expect(run!.links).toEqual([{ kind: 'Recipe', id: 'recipe-1', stepKey: null }]);
    expect(run!.nextAction!.actions[0]).toEqual({
      capabilityKey: 'recipes.edit',
      name: 'Open your recipe',
      route: 'recipes/recipe-1',
      available: true,
      youCanDoIt: true,
    });
    expect(run!.warnings[0].code).toBe('newer_version');
  });

  it('keeps a step draft only when both its kind and its id came', () => {
    const steps = (wireRun()['steps'] as Record<string, unknown>[]).map((step) => ({ ...step }));
    steps[1] = {
      ...steps[1],
      status: 'InProgress',
      draftKind: 'Recipe',
      draftResourceId: 'draft-1',
      draftSavedAt: '2026-10-15T09:20:00Z',
    };
    steps[2] = { ...steps[2], draftKind: 'Recipe' };

    const run = decodeWorkflowRun(wireRun({ steps }))!;

    expect(run.steps[1].draft).toEqual({ kind: 'Recipe', id: 'draft-1' });
    expect(run.steps[2].draft).toBeNull();
  });

  it('keeps a kind of record it has never heard of, rather than refusing the run', () => {
    const run = decodeWorkflowRun(wireRun({ links: [{ kind: 'SomethingNew', resourceId: 'x-1', stepKey: 'photo' }] }));

    expect(run!.links[0].kind).toBe('SomethingNew');
  });

  it('reads what to do next, with the record to start it from only when both its kind and its id came', () => {
    const run = decodeWorkflowRun(
      wireRun({
        nextSuggestions: [
          {
            kind: 'Workflow',
            key: 'recipe.test-and-approve',
            label: 'Test and approve a recipe',
            why: 'Cook it.',
            route: null,
            startFromKind: 'Recipe',
            startFromId: 'recipe-1',
            available: true,
            youCanDoIt: true,
          },
          {
            kind: 'Workflow',
            key: 'images.create',
            label: 'Create food images',
            why: 'Pictures.',
            route: null,
            startFromKind: 'Recipe',
            startFromId: null,
            available: true,
            youCanDoIt: true,
          },
          {
            kind: 'Capability',
            key: 'recipes.lead-picture',
            label: 'Choose the lead picture',
            why: 'First photo.',
            route: 'recipes/recipe-1?area=media',
            startFromKind: null,
            startFromId: null,
            available: true,
            youCanDoIt: false,
          },
        ],
      }),
    );

    expect(run!.nextSuggestions.map((suggestion) => suggestion.startFrom)).toEqual([
      { kind: 'Recipe', id: 'recipe-1' },
      null,
      null,
    ]);
    expect(run!.nextSuggestions[2]).toEqual(
      jasmine.objectContaining({ kind: 'Capability', route: 'recipes/recipe-1?area=media', youCanDoIt: false }),
    );
  });

  it('reads a run from a server that predates suggestions as having none, and refuses a suggestion that is half there', () => {
    expect(decodeWorkflowRun(wireRun())!.nextSuggestions).toEqual([]);
    expect(decodeWorkflowRun(wireRun({ nextSuggestions: [{ kind: 'Workflow', key: 'x' }] }))).toBeNull();
    expect(
      decodeWorkflowRun(
        wireRun({
          nextSuggestions: [{ kind: 'Elsewhere', key: 'x', label: 'X', why: 'Y', available: true, youCanDoIt: true }],
        }),
      ),
    ).toBeNull();
  });

  it('reads a run with no next action and an older server with no outcome', () => {
    const run = decodeWorkflowRun(wireRun({ nextAction: null, expectedOutcome: undefined, readyToComplete: true }));

    expect(run!.nextAction).toBeNull();
    expect(run!.expectedOutcome).toBeNull();
    expect(run!.readyToComplete).toBeTrue();
  });

  for (const [what, broken] of [
    ['not an object', 'run'],
    ['no token', wireRun({ concurrencyToken: undefined })],
    ['a status it does not know', wireRun({ status: 'Paused' })],
    ['a step with no key', wireRun({ steps: [{ title: 'x', requirement: 'Required', status: 'NotStarted' }] })],
    [
      'a step with a status it does not know',
      wireRun({ steps: [{ key: 'a', title: 'A', requirement: 'Required', status: 'Halfway' }] }),
    ],
    ['a next action that is half there', wireRun({ nextAction: { stepKey: 'a' } })],
    ['links that are not a list', wireRun({ links: {} })],
  ] as const) {
    it(`refuses a run with ${what}`, () => {
      expect(decodeWorkflowRun(broken)).toBeNull();
    });
  }

  it('reads what a refused completion says, and nothing that is not what it should be', () => {
    expect(decodeMissing({ missing: ['Save a change to the recipe first.'] })).toEqual([
      'Save a change to the recipe first.',
    ]);
    expect(decodeMissing({ missing: 'no' })).toEqual([]);
    expect(decodeMissing(null)).toEqual([]);

    expect(
      decodeIncompleteSteps({
        incompleteSteps: [
          { stepKey: 'ingredients', title: 'Ingredients', missing: ["This step isn't done yet."] },
          { stepKey: 7 },
        ],
      }),
    ).toEqual([{ stepKey: 'ingredients', title: 'Ingredients', missing: ["This step isn't done yet."] }]);
  });

  it('says where each step stands for the stepper', () => {
    const run = decodeWorkflowRun(wireRun())!;
    const skipped = { ...run.steps[2], status: 'Skipped' as const };

    expect(stepperStatusOf(run.steps[0], 'ingredients')).toBe('done');
    expect(stepperStatusOf(run.steps[1], 'ingredients')).toBe('current');
    expect(stepperStatusOf(run.steps[2], 'ingredients')).toBe('upcoming');
    expect(stepperStatusOf(skipped, 'ingredients')).toBe('skipped');
  });
});
