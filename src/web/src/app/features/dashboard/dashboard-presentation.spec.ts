import { DashboardWorkflowRun } from '../../models/dashboard.models';
import { attentionWords, runLinkLabel, runName } from './dashboard-presentation';

function run(overrides: Partial<DashboardWorkflowRun> = {}): DashboardWorkflowRun {
  return {
    id: 'w1',
    title: 'Create a recipe',
    definitionKey: 'recipe.create',
    goal: 'Create a recipe',
    stepKey: 'method',
    stepTitle: 'Check your method',
    youCanDoIt: true,
    updatedAt: '2026-10-14T11:00:00+00:00',
    attentionReasons: [],
    ...overrides,
  };
}

describe('runName', () => {
  it('keeps the creator’s own words for a run they named', () => {
    expect(runName(run({ title: 'Soda bread, start to finish' }))).toBe('Soda bread, start to finish');
  });

  it('names a run nobody named by the step it is on, because its title is only its goal', () => {
    expect(runName(run())).toBe('Create a recipe — Check your method');
  });

  it('reads a title that is its goal with space around it as the goal, not as a name', () => {
    expect(runName(run({ title: '  Create a recipe ' }))).toBe('  Create a recipe  — Check your method');
  });

  it('says there is nothing left but to finish it where there is no step', () => {
    expect(runName(run({ stepKey: null, stepTitle: null }))).toBe('Create a recipe — finish it');
  });

  it('says the same where the step has no title of its own, rather than ending on a dash', () => {
    for (const stepTitle of ['', '   ']) {
      expect(runName(run({ stepTitle }))).toBe('Create a recipe — finish it');
    }
  });
});

describe('runLinkLabel', () => {
  it('offers to carry on with a step the reader may take', () => {
    expect(runLinkLabel(run(), 'Oct 14')).toBe('Carry on with Create a recipe — Check your method, last touched Oct 14');
  });

  it('offers only to open one that is somebody else’s to carry on', () => {
    expect(runLinkLabel(run({ youCanDoIt: false }), 'Oct 13')).toBe('Open Create a recipe — Check your method, last touched Oct 13');
  });

  it('contains the name the creator can see, so the two agree (SC 2.5.3)', () => {
    const named = run({ title: 'Soda bread, start to finish' });
    expect(runLinkLabel(named, 'Oct 14')).toContain(runName(named));
  });

  it('tells two unnamed runs on one step apart by the day they were last touched', () => {
    expect(runLinkLabel(run({ id: 'a' }), 'Oct 14')).not.toBe(runLinkLabel(run({ id: 'b' }), 'Oct 13'));
  });
});

describe('attentionWords', () => {
  it('says each reason in ordinary words', () => {
    expect(attentionWords(['idle', 'newer_version'])).toEqual([
      'Nobody has touched it in over a week',
      'A newer version of this workflow has shipped since it started',
    ]);
  });

  it('leaves out a reason this build does not know rather than printing its code', () => {
    expect(attentionWords(['idle', 'something_new'])).toEqual(['Nobody has touched it in over a week']);
  });

  it('still says something when that leaves nothing, because a reason with no words reads as a fault', () => {
    for (const reasons of [[], ['something_new']]) {
      expect(attentionWords(reasons)).toEqual(['We could not say why. Open it to see where it stopped.']);
    }
  });
});
