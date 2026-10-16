import { acceptsFromElsewhere, applyPlace, commandFor, destinationsFor, listOf, placeOf, placesIn, tasksIn } from './my-week.moves';
import { fullWeek, task } from './my-week.fixtures';

const ids = (tasks: readonly { id: string }[]) => tasks.map((each) => each.id);

describe('My Week lists', () => {
  it('finds the list each task is in, and the task after it', () => {
    const week = fullWeek();

    expect(listOf(week, 'thu-1')).toBe('day-2038-10-14');
    expect(listOf(week, 'old')).toBe('carried-over');
    expect(listOf(week, 'done')).toBe('done');
    expect(listOf(week, 'nowhere')).toBeNull();
    expect(placeOf(week, 'thu-1')).toEqual({ list: 'day-2038-10-14', beforeTaskId: 'thu-2' });
    expect(placeOf(week, 'thu-2')).toEqual({ list: 'day-2038-10-14', beforeTaskId: null });
  });

  it('takes drops into every list but the two where tasks are only found', () => {
    expect(acceptsFromElsewhere('backlog')).toBeTrue();
    expect(acceptsFromElsewhere('day-2038-10-14')).toBeTrue();
    expect(acceptsFromElsewhere('done')).toBeTrue();
    expect(acceptsFromElsewhere('carried-over')).toBeFalse();
    expect(acceptsFromElsewhere('unplanned')).toBeFalse();
  });

  it('offers the backlog, each day with today said, the status lists, Done and next week as places to send a task', () => {
    const labels = destinationsFor(fullWeek()).map((option) => option.label);

    expect(labels[0]).toBe('Backlog — this week, without a day');
    expect(labels).toContain('Thu 14 Oct (today)');
    expect(labels).toContain('Done — marks it finished');
    expect(labels[labels.length - 1]).toBe('Next week’s backlog (week of Mon 18 Oct)');
    expect(labels).not.toContain('From earlier weeks');
  });

  it('offers a place before each task already in a list, never before the task itself, and the bottom', () => {
    expect(placesIn(fullWeek(), 'day-2038-10-14', 'thu-1')).toEqual([
      { value: 'thu-2', label: 'At the top, before “Shoot the galette”' },
      { value: '', label: 'At the bottom' },
    ]);
    expect(placesIn(fullWeek(), 'day-2038-10-15', 'thu-1')).toEqual([{ value: '', label: 'The only one there' }]);
    expect(placesIn(fullWeek(), 'done', 'thu-1')).toEqual([]);
    expect(placesIn(fullWeek(), 'next-week', 'thu-1')).toEqual([]);
  });
});

describe('My Week commands', () => {
  const week = fullWeek();
  const thursday = tasksIn(week, 'day-2038-10-14')[0];

  it('reorders within the list a task is already in — a move there is refused by the server', () => {
    expect(commandFor(week, thursday, { list: 'day-2038-10-14', beforeTaskId: null })).toEqual({ kind: 'reorder', beforeTaskId: null });
  });

  it('asks for nothing when the place is the one it already has', () => {
    expect(commandFor(week, thursday, { list: 'day-2038-10-14', beforeTaskId: 'thu-2' })).toEqual({ kind: 'none' });
  });

  it('moves to another list with the day, week or status that list means, and the place in it', () => {
    expect(commandFor(week, thursday, { list: 'day-2038-10-11', beforeTaskId: 'mon' })).toEqual({
      kind: 'move',
      request: { status: 'Backlog', scheduledDate: '2038-10-11', beforeTaskId: 'mon' },
    });
    expect(commandFor(week, thursday, { list: 'backlog', beforeTaskId: null })).toEqual({
      kind: 'move',
      request: { status: 'Backlog', planningWeekStart: '2038-10-11' },
    });
    expect(commandFor(week, thursday, { list: 'in-progress', beforeTaskId: 'going' })).toEqual({
      kind: 'move',
      request: { status: 'InProgress', beforeTaskId: 'going' },
    });
    expect(commandFor(week, thursday, { list: 'next-week', beforeTaskId: 'ignored' })).toEqual({
      kind: 'move',
      request: { status: 'Backlog', planningWeekStart: '2038-10-18' },
    });
  });

  it('completes a task sent to Done, and asks nothing of a finished one', () => {
    expect(commandFor(week, thursday, { list: 'done', beforeTaskId: null })).toEqual({ kind: 'complete' });
    expect(commandFor(week, tasksIn(week, 'done')[0], { list: 'backlog', beforeTaskId: null })).toEqual({ kind: 'none' });
  });

  it('sends a task back to the earlier week it came from, which is what Undo needs, and never out of every week', () => {
    const original = tasksIn(week, 'carried-over')[0];
    const after = applyPlace(week, original, { list: 'backlog', beforeTaskId: null });
    const moved = tasksIn(after, 'backlog').find((each) => each.id === original.id)!;

    expect(commandFor(after, moved, { list: 'carried-over', beforeTaskId: null }, original)).toEqual({
      kind: 'move',
      request: { status: 'Backlog', planningWeekStart: '2038-10-04' },
    });
    expect(commandFor(week, task({ id: 'x' }), { list: 'unplanned', beforeTaskId: null })).toEqual({ kind: 'none' });
  });
});

describe('My Week placement on screen', () => {
  const week = fullWeek();
  const thursday = tasksIn(week, 'day-2038-10-14')[0];

  it('takes the task out of its list and puts it before the task asked for, saying what it now is', () => {
    const after = applyPlace(week, thursday, { list: 'day-2038-10-11', beforeTaskId: 'mon' });

    expect(ids(tasksIn(after, 'day-2038-10-14'))).toEqual(['thu-2']);
    expect(ids(tasksIn(after, 'day-2038-10-11'))).toEqual(['thu-1', 'mon']);
    expect(tasksIn(after, 'day-2038-10-11')[0].scheduledDate).toBe('2038-10-11');
  });

  it('puts a task at the bottom when no place is named, and a finished one at the top of Done', () => {
    expect(ids(tasksIn(applyPlace(week, thursday, { list: 'waiting', beforeTaskId: null }), 'waiting'))).toEqual(['held', 'thu-1']);

    const done = applyPlace(week, thursday, { list: 'done', beforeTaskId: null });
    expect(ids(tasksIn(done, 'done'))).toEqual(['thu-1', 'done']);
    expect(tasksIn(done, 'done')[0].status).toBe('Done');
  });

  it('reorders within one list, and takes a task sent to next week off this one', () => {
    expect(ids(tasksIn(applyPlace(week, thursday, { list: 'day-2038-10-14', beforeTaskId: null }), 'day-2038-10-14'))).toEqual([
      'thu-2',
      'thu-1',
    ]);
    expect(listOf(applyPlace(week, thursday, { list: 'next-week', beforeTaskId: null }), 'thu-1')).toBeNull();
  });

  it('leaves every total as the server said it until the week is read again', () => {
    const after = applyPlace(week, thursday, { list: 'waiting', beforeTaskId: null });

    expect(after.days[3].totals).toEqual(week.days[3].totals);
    expect(after.waiting.totals).toEqual(week.waiting.totals);
  });
});
