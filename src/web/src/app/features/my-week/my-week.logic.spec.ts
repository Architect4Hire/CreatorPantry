import { NO_FILTERS, columnsFor, nothingShown, shownWords, totalsWords, weekHeading, weekIsEmpty } from './my-week.logic';
import { fullWeek, group, task, totals, week } from './my-week.fixtures';

const headings = (columns: ReturnType<typeof columnsFor>) => columns.map((column) => column.heading);
const ids = (columns: ReturnType<typeof columnsFor>) =>
  columns.flatMap((column) => column.sections.flatMap((section) => section.tasks.map((each) => each.id)));

describe('My Week columns', () => {
  it('keeps the compact board to five columns, with the days as sections of This week and today said in a word', () => {
    const columns = columnsFor(fullWeek(), 'compact', NO_FILTERS);

    expect(headings(columns)).toEqual(['Backlog', 'This week', 'In progress', 'Waiting', 'Done']);

    const days = columns[1].sections;
    expect(days.map((day) => day.heading)).toEqual([
      'Mon 11 Oct',
      'Tue 12 Oct',
      'Wed 13 Oct',
      'Thu 14 Oct',
      'Fri 15 Oct',
      'Sat 16 Oct',
      'Sun 17 Oct',
    ]);
    expect(days.find((day) => day.marker === 'Today')!.heading).toBe('Thu 14 Oct');
  });

  it('gives each day a column of its own in the Days view, and keeps today’s word in its heading', () => {
    const columns = columnsFor(fullWeek(), 'days', NO_FILTERS);

    expect(columns.length).toBe(11);
    expect(headings(columns)[4]).toBe('Thu 14 Oct · Today');
    expect(columns[4].sections[0].heading).toBeNull();
  });

  it('puts every task the server sent in exactly one place, in either view', () => {
    for (const view of ['compact', 'days'] as const) {
      const placed = ids(columnsFor(fullWeek(), view, NO_FILTERS));
      expect(placed.length).toBe(9);
      expect(new Set(placed).size).toBe(9);
    }
  });

  it('splits the backlog into this week, earlier weeks and no week, and shows earlier weeks only when it can hold any', () => {
    expect(columnsFor(fullWeek(), 'compact', NO_FILTERS)[0].sections.map((section) => section.heading)).toEqual([
      'Without a day',
      'From earlier weeks',
      'No week yet',
    ]);

    const ahead = columnsFor(week({ isCurrentWeek: false }), 'compact', NO_FILTERS)[0];
    expect(ahead.sections.map((section) => section.heading)).toEqual(['Without a day', 'No week yet']);
  });

  it('counts each column from the server’s totals, loaded or not', () => {
    const cut = fullWeek({ inProgress: group([task({ id: 'going', status: 'InProgress' })], { hasMore: true, totals: totals(63) }) });
    const inProgress = columnsFor(cut, 'compact', NO_FILTERS)[2];

    expect(inProgress.count).toBe(63);
    expect(shownWords(inProgress.sections[0], false)).toBe('Showing the first 1 of 63');
  });
});

describe('My Week filters', () => {
  it('narrows the loaded cards by words, priority and label, and says how many of the server’s it shows', () => {
    const byWords = columnsFor(fullWeek(), 'compact', { ...NO_FILTERS, query: 'GALETTE' });
    expect(ids(byWords)).toEqual(['thu-2']);

    const thursday = byWords[1].sections[3];
    expect(shownWords(thursday, true)).toBe('1 of 2 shown');
    expect(byWords[1].sections[0].empty).toBe('Nothing here matches these filters.');

    expect(ids(columnsFor(fullWeek(), 'compact', { ...NO_FILTERS, highOnly: true }))).toEqual(['thu-1']);

    // Over a list the server cut, a filter says it looked at the loaded ones only.
    const cut = fullWeek({
      inProgress: group([task({ id: 'a', title: 'Galette', status: 'InProgress' }), task({ id: 'b', status: 'InProgress', title: 'Soup' })], {
        hasMore: true,
        totals: totals(63),
      }),
    });
    expect(shownWords(columnsFor(cut, 'compact', { ...NO_FILTERS, query: 'galette' })[2].sections[0], true)).toBe(
      '1 of the first 2 shown — 63 in all',
    );
    expect(ids(columnsFor(fullWeek(), 'compact', { ...NO_FILTERS, labelId: 'tag-1' }))).toEqual(['thu-2']);
  });

  it('keeps each list’s own empty words where the filters had nothing to hide', () => {
    const friday = columnsFor(fullWeek(), 'compact', { ...NO_FILTERS, query: 'galette' })[1].sections[4];
    expect(friday.heading).toBe('Fri 15 Oct');
    expect(friday.empty).toBe('Nothing planned.');
  });

  it('knows a week the server sent nothing for from one the filters emptied', () => {
    expect(weekIsEmpty(week())).toBeTrue();
    expect(weekIsEmpty(fullWeek())).toBeFalse();
    expect(nothingShown(columnsFor(fullWeek(), 'compact', { ...NO_FILTERS, query: 'nothing like this' }))).toBeTrue();
  });
});

describe('My Week words', () => {
  it('says what a group adds up to without treating an unknown estimate as no time', () => {
    expect(totalsWords(totals(0))).toBe('Nothing planned');
    expect(totalsWords(totals(3, 2, 150))).toBe('3 tasks · 2 hr 30 min estimated · 1 not estimated');
    expect(totalsWords(totals(2, 2, 60))).toBe('2 tasks · 1 hr estimated');
    expect(totalsWords(totals(4))).toBe('4 tasks · none estimated');
    expect(totalsWords(totals(1))).toBe('1 task · not estimated');
  });

  it('names a week by its Monday', () => {
    expect(weekHeading('2038-10-11')).toBe('Week of Mon 11 Oct');
  });
});
