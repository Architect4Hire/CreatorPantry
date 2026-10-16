import { RUN, TODAY, day, fullDay, task } from './my-day.fixtures';
import { estimateWords, groupOf, layoutDay, linkFor, moveBack, movesFor, topThreeCandidates } from './my-day.logic';

const ids = (entries: readonly { task: { id: string } }[]) => entries.map((entry) => entry.task.id);

describe('My Day layout', () => {
  it('puts the first overdue task up next, and takes it out of its group so it is listed once', () => {
    const layout = layoutDay(fullDay());

    expect(layout.lead?.kind).toBe('next');
    expect(ids(layout.lead!.entries)).toEqual(['late']);
    expect(layout.lead!.entries[0].group).toBe('overdue');
    expect(ids(layout.groups.find((group) => group.definition.key === 'overdue')!.entries)).toEqual([]);

    const everywhere = [...ids(layout.lead!.entries), ...layout.groups.flatMap((group) => ids(group.entries))];
    expect(everywhere.length).toBe(new Set(everywhere).size);
    expect(everywhere.length).toBe(6);
  });

  it('prefers what is under way to what is merely planned, and never offers a waiting task as next', () => {
    expect(ids(layoutDay(fullDay({ overdue: [] })).lead!.entries)).toEqual(['going']);
    expect(ids(layoutDay(fullDay({ overdue: [], inProgress: [] })).lead!.entries)).toEqual(['plan-1']);
    expect(layoutDay(day({ waiting: [task({ id: 'held', status: 'Waiting' })] })).lead).toBeNull();
  });

  it('leads with the creator’s own top three, in their order, from whichever groups they are in', () => {
    const layout = layoutDay(fullDay({ topThree: ['plan-2', 'held', 'done'] }));

    expect(layout.lead?.kind).toBe('top-three');
    expect(ids(layout.lead!.entries)).toEqual(['plan-2', 'held', 'done']);
    expect(layout.lead!.entries.map((entry) => entry.group)).toEqual(['scheduledToday', 'waiting', 'completedToday']);

    // Each is in its group no longer, and the overdue task is back in its own group rather than up next.
    expect(ids(layout.groups.find((group) => group.definition.key === 'scheduledToday')!.entries)).toEqual(['plan-1']);
    expect(ids(layout.groups.find((group) => group.definition.key === 'overdue')!.entries)).toEqual(['late']);
  });

  it('says when nothing is left to do, and carries each group’s cut flag through', () => {
    expect(layoutDay(day()).nothingOpen).toBeTrue();
    expect(layoutDay(day({ completedToday: [task({ status: 'Done' })] })).nothingOpen).toBeTrue();
    expect(layoutDay(day({ waiting: [task({ status: 'Waiting' })] })).nothingOpen).toBeFalse();

    const cut = layoutDay(fullDay({ scheduledTodayHasMore: true }));
    expect(cut.groups.find((group) => group.definition.key === 'scheduledToday')!.hasMore).toBeTrue();
  });

  it('offers everything left to do as a top three, and a finished task only while it is chosen', () => {
    expect(ids(topThreeCandidates(fullDay()))).toEqual(['late', 'plan-1', 'plan-2', 'going', 'held']);
    expect(ids(topThreeCandidates(fullDay({ topThree: ['done'] })))).toContain('done');
  });

  it('finds the group a task is in, or none', () => {
    expect(groupOf(fullDay(), 'held')).toBe('waiting');
    expect(groupOf(fullDay(), 'nowhere')).toBeNull();
  });
});

describe('My Day moves', () => {
  it('offers a not-started task the four simple places, and next week on the last day of this one', () => {
    const moves = movesFor(task(), TODAY);
    expect(moves.map((move) => move.id)).toEqual(['in-progress', 'waiting', 'tomorrow', 'later-this-week']);
    expect(moves[2].request).toEqual({ status: 'Backlog', scheduledDate: '2038-10-15' });
    expect(moves[3].request).toEqual({ status: 'Backlog', planningWeekStart: '2038-10-11' });

    const sunday = movesFor(task({ scheduledDate: '2038-10-17' }), '2038-10-17');
    expect(sunday[3]).toEqual({ id: 'next-week', label: 'Next week', request: { status: 'Backlog', planningWeekStart: '2038-10-18' } });
  });

  it('never sends a day or week with in progress or waiting, and offers nothing for a finished task', () => {
    for (const move of [...movesFor(task(), TODAY), ...movesFor(task({ status: 'InProgress' }), TODAY), ...movesFor(task({ status: 'Waiting' }), TODAY)]) {
      if (move.request.status !== 'Backlog') {
        expect(move.request.scheduledDate).toBeUndefined();
        expect(move.request.planningWeekStart).toBeUndefined();
      }
    }

    expect(movesFor(task({ status: 'InProgress' }), TODAY).map((move) => move.id)).toEqual(['waiting', 'not-started', 'tomorrow']);
    expect(movesFor(task({ status: 'Waiting' }), TODAY).map((move) => move.id)).toEqual(['in-progress', 'tomorrow']);
    expect(movesFor(task({ status: 'Done' }), TODAY)).toEqual([]);
  });

  it('puts a task back where it was: its day, else its week, and nothing else for a started one', () => {
    expect(moveBack(task())).toEqual({ status: 'Backlog', scheduledDate: TODAY });
    expect(moveBack(task({ scheduledDate: null }))).toEqual({ status: 'Backlog', planningWeekStart: '2038-10-11' });
    expect(moveBack(task({ scheduledDate: null, planningWeekStart: null }))).toEqual({ status: 'Backlog' });
    expect(moveBack(task({ status: 'Waiting' }))).toEqual({ status: 'Waiting' });
    expect(moveBack(task({ status: 'Done' }))).toBeNull();
  });
});

describe('My Day links', () => {
  const links = (overrides: object) => task({ links: { ...task().links, ...overrides } });

  it('opens the workflow first, then the recipe, the card, the picture', () => {
    expect(linkFor(links({ workflowRunRoute: `workflows/runs/${RUN}`, recipeId: 'r-1' }), 'sams-kitchen')).toEqual({
      commands: ['/', 'sams-kitchen', 'workflows', 'runs', RUN],
      what: 'the workflow',
    });
    expect(linkFor(links({ recipeId: 'r-1', contentCardId: 'c-1' }), 'sams-kitchen')!.commands).toEqual(['/', 'sams-kitchen', 'recipes', 'r-1']);
    expect(linkFor(links({ contentCardId: 'c-1' }), 'sams-kitchen')!.commands).toEqual(['/', 'sams-kitchen', 'content-board', 'c-1']);
    expect(linkFor(links({ mediaAssetId: 'a-1' }), 'sams-kitchen')!.commands).toEqual(['/', 'sams-kitchen', 'dam', 'a-1']);
    expect(linkFor(task(), 'sams-kitchen')).toBeNull();
  });

  it('does not follow a run address that is not the shape the server builds', () => {
    expect(linkFor(links({ workflowRunRoute: 'https://elsewhere.test/x' }), 'sams-kitchen')).toBeNull();
    expect(linkFor(links({ workflowRunRoute: '../../admin' }), 'sams-kitchen')).toBeNull();
  });
});

describe('estimateWords', () => {
  it('says an estimate in words, and nothing for an unknown one', () => {
    expect(estimateWords(30)).toBe('30 min');
    expect(estimateWords(60)).toBe('1 hr');
    expect(estimateWords(90)).toBe('1 hr 30 min');
    expect(estimateWords(null)).toBeNull();
  });
});
