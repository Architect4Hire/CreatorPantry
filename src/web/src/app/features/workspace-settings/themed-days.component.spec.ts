import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import { ThemedDayHistoryItem, WeeklyTheme, WeeklyThemeInput } from '../../models/weekly-theme.models';
import {
  DeleteWeeklyThemeOutcome,
  ReplaceWeeklyThemesOutcome,
  ThemedDayHistoryOutcome,
  WeeklyThemeWeekOutcome,
  WeeklyThemesService,
} from '../../services/weekly-themes.service';
import { WorkspaceOutcome, WorkspaceSettingsService } from '../../services/workspace-settings.service';
import { ThemedDaysComponent } from './themed-days.component';

function theme(overrides: Partial<WeeklyTheme> = {}): WeeklyTheme {
  return {
    day: 'Monday',
    key: 'meat-free-monday',
    displayName: 'Meat-free Monday',
    description: 'No meat.',
    standingNotes: 'Always under 30 minutes.\nNever tofu.',
    retiredAt: null,
    revision: 1,
    ...overrides,
  };
}

function historyItem(overrides: Partial<ThemedDayHistoryItem> = {}): ThemedDayHistoryItem {
  return {
    contextId: 'c1',
    workingTitle: 'Lentil ragu',
    isArchived: false,
    latestOutcomeAt: '2026-10-05T09:00:00+00:00',
    outcomes: [
      {
        kind: 'AcceptedPosts',
        at: '2026-10-05T09:00:00+00:00',
        mediaAssetId: null,
        channelKeys: ['instagram'],
        needsReview: true,
        recipeId: null,
      },
    ],
    ...overrides,
  };
}

class StubThemes {
  week: readonly WeeklyTheme[] = [theme()];
  weekOutcome: WeeklyThemeWeekOutcome | null = null;
  replaceOutcome: ReplaceWeeklyThemesOutcome | null = null;
  deleteOutcome: DeleteWeeklyThemeOutcome | null = null;
  historyOutcomes: ThemedDayHistoryOutcome[] = [];

  readonly replaces: (readonly WeeklyThemeInput[])[] = [];
  readonly deletes: string[] = [];
  readonly historyReads: { key: string; cursor: string | null }[] = [];
  /** Set to leave the week's read, or a history's, unanswered, so the page stays loading there. */
  holdWeek = false;
  holdHistory = false;

  getWeek(): Promise<WeeklyThemeWeekOutcome> {
    if (this.holdWeek) return new Promise(() => undefined);
    return Promise.resolve(this.weekOutcome ?? { status: 'found', themes: this.week });
  }

  replaceWeek(_slug: string, themes: readonly WeeklyThemeInput[]): Promise<ReplaceWeeklyThemesOutcome> {
    this.replaces.push(themes);
    if (this.replaceOutcome) return Promise.resolve(this.replaceOutcome);

    // What the server does: submitted themes are live, and a live one left out is retired.
    const submitted = new Set(themes.map((entry) => entry.key));
    this.week = [
      ...themes.map((entry) => theme({ ...entry, retiredAt: null })),
      ...this.week
        .filter((stored) => !submitted.has(stored.key))
        .map((stored) => ({ ...stored, retiredAt: stored.retiredAt ?? '2026-10-11T08:00:00+00:00' })),
    ];
    return Promise.resolve({ status: 'saved', themes: this.week });
  }

  deleteTheme(_slug: string, key: string): Promise<DeleteWeeklyThemeOutcome> {
    this.deletes.push(key);
    if (this.deleteOutcome) return Promise.resolve(this.deleteOutcome);

    this.week = this.week.filter((stored) => stored.key !== key);
    return Promise.resolve({ status: 'deleted', themes: this.week });
  }

  getHistory(_slug: string, key: string, options: { cursor?: string | null } = {}): Promise<ThemedDayHistoryOutcome> {
    this.historyReads.push({ key, cursor: options.cursor ?? null });
    if (this.holdHistory) return new Promise(() => undefined);
    return Promise.resolve(
      this.historyOutcomes.shift() ?? { status: 'found', page: { items: [], nextCursor: null } },
    );
  }
}

class StubWorkspaces {
  role: WorkspaceRole = 'Owner';
  outcome: WorkspaceOutcome | null = null;

  get(): Promise<WorkspaceOutcome> {
    return Promise.resolve(
      this.outcome ?? {
        status: 'found',
        workspace: {
          workspaceId: 'w1',
          name: "Sam's Kitchen",
          slug: 'sams-kitchen',
          role: this.role,
          defaultMeasurementSystem: 'UsCustomary',
        },
      },
    );
  }
}

class StubConfirm {
  answer = true;
  readonly asked: ConfirmRequest[] = [];

  confirm(request: ConfirmRequest): Promise<boolean> {
    this.asked.push(request);
    return Promise.resolve(this.answer);
  }
}

describe('ThemedDaysComponent', () => {
  let fixture: ComponentFixture<unknown>;
  let component: ThemedDaysComponent;
  let themes: StubThemes;
  let workspaces: StubWorkspaces;
  let confirm: StubConfirm;

  async function render(): Promise<HTMLElement> {
    const harness = await RouterTestingHarness.create();
    component = await harness.navigateByUrl('/sams-kitchen/settings/themed-days', ThemedDaysComponent);
    fixture = harness.fixture;
    await settle();
    return harness.routeNativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function sections(host: HTMLElement): HTMLElement[] {
    return Array.from(host.querySelectorAll<HTMLElement>('cp-form-section'));
  }

  function section(host: HTMLElement, heading: string): HTMLElement {
    const found = sections(host).find((candidate) => candidate.textContent?.includes(heading));
    if (!found) throw new Error(`No section headed ${heading}`);
    return found;
  }

  function button(scope: HTMLElement, label: string): HTMLButtonElement {
    const found = Array.from(scope.querySelectorAll<HTMLButtonElement>('button')).find((candidate) =>
      candidate.textContent?.trim().startsWith(label),
    );
    if (!found) throw new Error(`No button labelled ${label}`);
    return found;
  }

  function hasButton(scope: HTMLElement, label: string): boolean {
    return Array.from(scope.querySelectorAll('button')).some((candidate) =>
      candidate.textContent?.trim().startsWith(label),
    );
  }

  function field(host: HTMLElement, day: string, name: string): HTMLInputElement | HTMLTextAreaElement {
    return host.querySelector(`[id="themed-day-${day}-${name}"]`) as HTMLInputElement | HTMLTextAreaElement;
  }

  async function type(control: HTMLInputElement | HTMLTextAreaElement, value: string): Promise<void> {
    control.value = value;
    control.dispatchEvent(new Event('input'));
    await settle();
  }

  async function press(target: HTMLButtonElement): Promise<void> {
    target.click();
    await settle();
  }

  function saveButton(host: HTMLElement): HTMLButtonElement {
    return host.querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  async function open(details: HTMLDetailsElement): Promise<void> {
    details.open = true;
    details.dispatchEvent(new Event('toggle'));
    await settle();
  }

  beforeEach(() => {
    themes = new StubThemes();
    workspaces = new StubWorkspaces();
    confirm = new StubConfirm();

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/settings/themed-days', component: ThemedDaysComponent }]),
        { provide: WeeklyThemesService, useValue: themes },
        { provide: WorkspaceSettingsService, useValue: workspaces },
        { provide: ConfirmService, useValue: confirm },
      ],
    });
  });

  // ---- Loading and first use ----

  it('draws the week arriving while it is read, and says so once from a region that is always there (AU.6.4)', async () => {
    themes.holdWeek = true;

    const host = await render();

    const status = host.querySelector('section.themed-days > p.cp-sr-only[role="status"]')!;
    expect(status.textContent).toBe('Loading your themed days…');
    const busy = host.querySelector('cp-card .loading[aria-busy="true"]')!;
    expect(busy.querySelectorAll('cp-skeleton').length).toBe(4);
    expect(busy.textContent!.trim()).toBe('');
    expect(host.querySelector('form')).toBeNull();
  });

  it('empties that status once the week is read', async () => {
    const host = await render();

    expect(host.querySelector('section.themed-days > p.cp-sr-only[role="status"]')!.textContent).toBe('');
    expect(host.querySelector('.loading')).toBeNull();
  });

  it('says the workspace could not be found, or offers another try, when the week cannot be read', async () => {
    themes.weekOutcome = { status: 'unavailable' };

    const host = await render();

    expect(host.querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");
    expect(hasButton(host, 'Try again')).toBeTrue();
    expect(host.querySelector('form')).toBeNull();
  });

  it('shows a first-use empty state rather than seven blank forms, and the form once the creator starts', async () => {
    themes.week = [];

    const host = await render();

    expect(host.querySelector('cp-empty-state')).not.toBeNull();
    // Drawn, and roomy: the first use invites the week (AU.6.4).
    expect(host.querySelector('cp-empty-state cp-illustration')?.getAttribute('data-name')).toBe('calendar');
    expect(host.querySelector('cp-empty-state')?.getAttribute('data-variant')).toBe('first-use');
    expect(sections(host).length).toBe(0);

    await press(button(host, 'Set up your week'));

    expect(host.querySelector('cp-empty-state')).toBeNull();
    expect(sections(host).map((entry) => entry.querySelector('h2, h3')?.textContent?.trim())).toEqual([
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ]);
    // Days without a theme are a line and a button, not empty inputs.
    expect(host.querySelectorAll('input, textarea').length).toBe(0);
    expect(saveButton(host).disabled).toBeTrue();
  });

  it('gives a member who cannot edit the empty state without a way to start', async () => {
    themes.week = [];
    workspaces.role = 'Viewer';

    const host = await render();

    expect(host.querySelector('cp-empty-state')).not.toBeNull();
    expect(hasButton(host, 'Set up your week')).toBeFalse();
    expect(host.textContent).toContain('An Editor or Owner of this workspace can set up themed days.');
  });

  // ---- The week ----

  it('shows each themed day with its name, description and standing notes, and the rest as no theme', async () => {
    const host = await render();

    expect(field(host, 'monday', 'displayName').value).toBe('Meat-free Monday');
    expect(field(host, 'monday', 'description').value).toBe('No meat.');
    expect(field(host, 'monday', 'standingNotes').value).toBe('Always under 30 minutes.\nNever tofu.');
    expect(section(host, 'Tuesday').textContent).toContain('No theme.');
    expect(field(host, 'tuesday', 'displayName')).toBeNull();
    expect(saveButton(host).disabled).toBeTrue();
  });

  it('names every field and marks the theme name as required for assistive technology', async () => {
    const host = await render();

    const name = field(host, 'monday', 'displayName');
    expect(host.querySelector(`label[for="${name.id}"]`)?.textContent).toContain('Theme name');
    expect(name.getAttribute('aria-required')).toBe('true');

    const notes = field(host, 'monday', 'standingNotes');
    expect(host.querySelector(`label[for="${notes.id}"]`)?.textContent).toContain('Standing notes');
    // The character count is the hint, and the hint describes the control.
    const describedBy = notes.getAttribute('aria-describedby') ?? '';
    expect(host.querySelector(`[id="${describedBy.split(' ')[0]}"]`)?.textContent).toContain('of 2000 characters');
  });

  it('saves an edit to the notes alone under the theme’s own key, then reads as clean', async () => {
    const host = await render();

    await type(field(host, 'monday', 'standingNotes'), 'Never tofu.  ');
    expect(saveButton(host).disabled).toBeFalse();
    expect(host.textContent).toContain('Unsaved changes');

    await press(saveButton(host));

    expect(themes.replaces).toEqual([
      [
        {
          day: 'Monday',
          key: 'meat-free-monday',
          displayName: 'Meat-free Monday',
          description: 'No meat.',
          standingNotes: 'Never tofu.',
        },
      ],
    ]);
    expect(host.querySelector('.result[aria-live="polite"]')?.textContent).toContain('Saved your week.');
    expect(saveButton(host).disabled).toBeTrue();
  });

  it('keeps a theme’s key when it is renamed', async () => {
    const host = await render();

    await type(field(host, 'monday', 'displayName'), 'Meatless Monday');
    await press(saveButton(host));

    expect(themes.replaces[0][0].key).toBe('meat-free-monday');
    expect(themes.replaces[0][0].displayName).toBe('Meatless Monday');
  });

  it('adds a theme to a free day with a key made from its name', async () => {
    const host = await render();

    await press(button(section(host, 'Tuesday'), 'Add a theme'));
    await type(field(host, 'tuesday', 'displayName'), 'Taco Tuesday!');
    await press(saveButton(host));

    expect(themes.replaces[0].map((entry) => [entry.day, entry.key])).toEqual([
      ['Monday', 'meat-free-monday'],
      ['Tuesday', 'taco-tuesday'],
    ]);
    expect(themes.replaces[0][1].description).toBeNull();
    expect(themes.replaces[0][1].standingNotes).toBeNull();
  });

  it('does not reuse a retired theme’s key for a new theme of the same name', async () => {
    themes.week = [theme({ retiredAt: '2026-10-01T08:00:00+00:00' })];
    const host = await render();

    await press(button(section(host, 'Monday'), 'Add a theme'));
    await type(field(host, 'monday', 'displayName'), 'Meat-free Monday');
    await press(saveButton(host));

    expect(themes.replaces[0][0].key).toBe('meat-free-monday-2');
  });

  // ---- Validation ----

  it('refuses a themed day with no name beside the field, focuses it, and sends nothing', async () => {
    const host = await render();

    await press(button(section(host, 'Wednesday'), 'Add a theme'));
    await press(saveButton(host));

    const name = field(host, 'wednesday', 'displayName');
    expect(themes.replaces.length).toBe(0);
    expect(name.getAttribute('aria-invalid')).toBe('true');
    expect(section(host, 'Wednesday').querySelector('[role="alert"]')?.textContent).toContain('Give Wednesday’s theme a name');
    expect(document.activeElement).toBe(name);
    expect(host.querySelector('.result[aria-live="polite"]')?.textContent).toContain('needs another look');

    // Typing in the field takes its error away.
    await type(name, 'Soup day');
    expect(name.getAttribute('aria-invalid')).toBeNull();
  });

  it('puts a server refusal beside the field it is about, by the day that was submitted', async () => {
    themes.week = [theme(), theme({ day: 'Friday', key: 'fakeaway-friday', displayName: 'Fakeaway Friday' })];
    themes.replaceOutcome = {
      status: 'validation_failed',
      fieldErrors: { 'themes[1].standingNotes': ['Standing notes can be at most 2000 characters.'] },
    };
    const host = await render();

    await type(field(host, 'friday', 'standingNotes'), 'x');
    await press(saveButton(host));

    expect(section(host, 'Friday').querySelector('[role="alert"]')?.textContent).toContain('at most 2000');
    expect(section(host, 'Monday').querySelector('[role="alert"]')).toBeNull();
    // The creator's edit is still there to correct.
    expect(field(host, 'friday', 'standingNotes').value).toBe('x');
    expect(saveButton(host).disabled).toBeFalse();
  });

  it('keeps the creator’s edits and offers a reload when the week changed somewhere else', async () => {
    themes.replaceOutcome = { status: 'conflict' };
    const host = await render();

    await type(field(host, 'monday', 'description'), 'Changed.');
    await press(saveButton(host));

    expect(host.querySelector('.result[aria-live="polite"]')?.textContent).toContain('changed somewhere else');
    expect(field(host, 'monday', 'description').value).toBe('Changed.');

    // Reloading replaces their edits, so it asks first.
    confirm.answer = false;
    await press(button(host, 'Reload the week'));
    expect(confirm.asked.length).toBe(1);
    expect(field(host, 'monday', 'description').value).toBe('Changed.');
  });

  it('says a retired theme must be deleted when the workspace is holding as many as it may', async () => {
    themes.replaceOutcome = { status: 'limit' };
    const host = await render();

    await type(field(host, 'monday', 'description'), 'Changed.');
    await press(saveButton(host));

    expect(host.querySelector('.result[aria-live="polite"]')?.textContent).toContain('Delete a retired theme to make room');
  });

  // ---- Retiring and bringing back ----

  it('takes a theme off the week on save, and lists it as retired rather than removing it', async () => {
    const host = await render();

    await press(button(section(host, 'Monday'), 'Take off the week'));

    expect(section(host, 'Monday').textContent).toContain('No theme.');
    expect(section(host, 'Retired themes').textContent).toContain('Meat-free Monday');
    expect(section(host, 'Retired themes').textContent).toContain('Comes off the week when you save');
    expect(themes.replaces.length).toBe(0);

    await press(saveButton(host));

    expect(themes.replaces).toEqual([[]]);
    expect(section(host, 'Retired themes').textContent).toContain('Retired');
    expect(section(host, 'Retired themes').textContent).not.toContain('Comes off the week when you save');
  });

  it('brings a retired theme back to its day with everything it had', async () => {
    themes.week = [theme({ retiredAt: '2026-10-01T08:00:00+00:00' })];
    const host = await render();

    await press(button(section(host, 'Retired themes'), 'Bring back'));

    expect(field(host, 'monday', 'standingNotes').value).toBe('Always under 30 minutes.\nNever tofu.');
    expect(sections(host).some((entry) => entry.textContent?.includes('Retired themes'))).toBeFalse();

    await press(saveButton(host));
    expect(themes.replaces[0][0].key).toBe('meat-free-monday');
  });

  it('will not bring a theme back onto a day that has one, and says why', async () => {
    themes.week = [
      theme({ key: 'pasta-monday', displayName: 'Pasta Monday' }),
      theme({ retiredAt: '2026-10-01T08:00:00+00:00' }),
    ];
    const host = await render();

    const bringBack = button(section(host, 'Retired themes'), 'Bring back');

    expect(bringBack.disabled).toBeTrue();
    const reason = host.querySelector(`[id="${bringBack.getAttribute('aria-describedby')}"]`);
    expect(reason?.textContent).toContain('Monday has a theme');
  });

  // ---- Deleting ----

  it('asks before deleting, says what happens to work that used the theme, and does nothing on "keep it"', async () => {
    confirm.answer = false;
    const host = await render();

    await press(button(section(host, 'Monday'), 'Delete permanently'));

    expect(themes.deletes.length).toBe(0);
    expect(confirm.asked[0].title).toContain('Meat-free Monday');
    expect(confirm.asked[0].message).toContain('Work you already made for this theme is not deleted');
    expect(confirm.asked[0].message).toContain('will no longer show this theme');
    expect(confirm.asked[0].message).toContain('take it off the week instead');
    expect(field(host, 'monday', 'displayName').value).toBe('Meat-free Monday');
  });

  it('deletes a theme at once when confirmed, and leaves other unsaved edits as they were', async () => {
    themes.week = [theme(), theme({ day: 'Friday', key: 'fakeaway-friday', displayName: 'Fakeaway Friday' })];
    const host = await render();

    await type(field(host, 'friday', 'description'), 'Takeaway favourites.');
    await press(button(section(host, 'Monday'), 'Delete permanently'));

    expect(themes.deletes).toEqual(['meat-free-monday']);
    expect(section(host, 'Monday').textContent).toContain('No theme.');
    expect(host.querySelector('.result[aria-live="polite"]')?.textContent).toContain('Deleted “Meat-free Monday”.');

    // Friday's edit was not saved and was not lost.
    expect(field(host, 'friday', 'description').value).toBe('Takeaway favourites.');
    expect(saveButton(host).disabled).toBeFalse();
    expect(themes.replaces.length).toBe(0);
  });

  it('says so and changes nothing when a delete fails', async () => {
    themes.deleteOutcome = { status: 'unavailable' };
    const host = await render();

    await press(button(section(host, 'Monday'), 'Delete permanently'));

    expect(host.querySelector('.result[aria-live="polite"]')?.textContent).toContain('couldn’t delete');
    expect(field(host, 'monday', 'displayName').value).toBe('Meat-free Monday');
  });

  it('offers deleting to an Owner only', async () => {
    workspaces.role = 'Editor';
    themes.week = [theme(), theme({ day: 'Friday', key: 'old', displayName: 'Old', retiredAt: '2026-10-01T08:00:00+00:00' })];

    const host = await render();

    expect(hasButton(host, 'Delete permanently')).toBeFalse();
    expect(hasButton(section(host, 'Monday'), 'Take off the week')).toBeTrue();
    expect(hasButton(section(host, 'Retired themes'), 'Bring back')).toBeTrue();
  });

  // ---- Reading only ----

  it('shows a member who cannot edit the week as text, with no controls', async () => {
    workspaces.role = 'Contributor';

    const host = await render();

    expect(host.querySelector('form')).toBeNull();
    expect(host.querySelectorAll('input, textarea').length).toBe(0);
    expect(host.textContent).toContain('An Editor or Owner can change them.');
    expect(section(host, 'Monday').textContent).toContain('Meat-free Monday');
    expect(section(host, 'Monday').textContent).toContain('Never tofu.');
    expect(section(host, 'Tuesday').textContent).toContain('No theme.');
    // History is a read, so it is still offered.
    expect(section(host, 'Monday').querySelector('details')).not.toBeNull();
  });

  // ---- History ----

  it('reads a theme’s history only when its disclosure is opened, and lists what was made', async () => {
    themes.historyOutcomes = [
      { status: 'found', page: { items: [historyItem()], nextCursor: 'next' } },
      { status: 'found', page: { items: [historyItem({ contextId: 'c2', workingTitle: null })], nextCursor: null } },
    ];
    const host = await render();
    const details = section(host, 'Monday').querySelector('details') as HTMLDetailsElement;

    expect(themes.historyReads.length).toBe(0);
    expect(details.querySelector('summary')?.textContent).toContain('Already made for Meat-free Monday');
    // The library's disclosure, which is what gives the summary its 40px target.
    expect(details.classList).toContain('cp-disclosure');

    await open(details);

    expect(themes.historyReads).toEqual([{ key: 'meat-free-monday', cursor: null }]);
    const link = details.querySelector('.history-head a') as HTMLAnchorElement;
    expect(link.textContent).toContain('Lentil ragu');
    expect(link.getAttribute('href')).toBe('/sams-kitchen/workflows/content-pipeline/context/c1');
    expect(details.textContent).toContain('Accepted posts for instagram');
    // Status in words, not colour alone.
    expect(details.textContent).toContain('Needs review');

    await press(button(details, 'Show more'));

    expect(themes.historyReads[1]).toEqual({ key: 'meat-free-monday', cursor: 'next' });
    expect(details.querySelectorAll('.history-item').length).toBe(2);
    expect(details.textContent).toContain('Untitled work');
    expect(hasButton(details, 'Show more')).toBeFalse();

    // Closing and reopening does not read it again.
    await open(details);
    expect(themes.historyReads.length).toBe(2);
  });

  it('draws what was made arriving while a history is read, its status kept once it is (AU.6.4)', async () => {
    themes.holdHistory = true;
    const host = await render();
    const details = section(host, 'Monday').querySelector('details') as HTMLDetailsElement;

    await open(details);

    const status = details.querySelector('p.cp-sr-only[role="status"]')!;
    expect(status.textContent).toBe("Loading what you've made…");
    expect(details.querySelector('[aria-busy="true"] cp-skeleton')).not.toBeNull();
    expect(details.textContent).not.toContain('Nothing made for this theme');
  });

  it('keeps a history’s status, emptied, once what was made is read', async () => {
    const host = await render();
    const details = section(host, 'Monday').querySelector('details') as HTMLDetailsElement;

    await open(details);

    expect(details.querySelector('p.cp-sr-only[role="status"]')!.textContent).toBe('');
    expect(details.querySelector('cp-skeleton')).toBeNull();
    // An empty history is one quiet line, not a drawing: it is part of a form (AU.6.4).
    expect(details.textContent).toContain('Nothing made for this theme in the last year yet.');
    expect(details.querySelector('cp-empty-state')).toBeNull();
  });

  it('says when nothing was made, and offers another try when the history cannot be read', async () => {
    themes.week = [theme(), theme({ day: 'Friday', key: 'fakeaway-friday', displayName: 'Fakeaway Friday' })];
    themes.historyOutcomes = [
      { status: 'found', page: { items: [], nextCursor: null } },
      { status: 'unavailable' },
      { status: 'found', page: { items: [historyItem()], nextCursor: null } },
    ];
    const host = await render();

    const monday = section(host, 'Monday').querySelector('details') as HTMLDetailsElement;
    await open(monday);
    expect(monday.textContent).toContain('Nothing made for this theme in the last year yet.');

    const friday = section(host, 'Friday').querySelector('details') as HTMLDetailsElement;
    await open(friday);
    expect(friday.textContent).toContain("couldn't be loaded");

    await press(button(friday, 'Try again'));
    expect(friday.textContent).toContain('Lentil ragu');
  });

  it('offers no history for a theme that has not been saved yet', async () => {
    const host = await render();

    await press(button(section(host, 'Tuesday'), 'Add a theme'));

    expect(section(host, 'Tuesday').querySelector('details')).toBeNull();
  });

  // ---- Leaving ----

  it('lets a clean form be left without asking, and asks once when there are unsaved edits', async () => {
    const host = await render();

    expect(await component.confirmDiscardIfDirty()).toBeTrue();
    expect(confirm.asked.length).toBe(0);

    await type(field(host, 'monday', 'description'), 'Changed.');
    confirm.answer = false;

    expect(await component.confirmDiscardIfDirty()).toBeFalse();
    expect(confirm.asked.length).toBe(1);
    expect(confirm.asked[0].title).toBe('Discard unsaved changes?');
  });

  it('offers one link back, to Workspace settings, in the page head and in every state', async () => {
    const host = await render();

    const crumb = host.querySelector('cp-page-head a[cpBackLink]') as HTMLAnchorElement;
    expect(crumb.textContent).toContain('Workspace settings');
    expect(crumb.textContent).not.toContain('Back to');
    expect(crumb.getAttribute('href')).toBe('/sams-kitchen/settings');
    expect(host.querySelectorAll('h1').length).toBe(1);
  });
});
