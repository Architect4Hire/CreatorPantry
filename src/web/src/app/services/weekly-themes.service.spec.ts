import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { weeklyThemeKeyFor } from '../models/weekly-theme.models';
import { WeeklyThemesService } from './weekly-themes.service';

const THEMES_URL = 'https://gateway.example/api/v1/workspaces/sams-kitchen/weekly-themes';

function wireTheme(overrides: Record<string, unknown> = {}) {
  return {
    day: 'Monday',
    key: 'meat-free-monday',
    displayName: 'Meat-free Monday',
    description: null,
    standingNotes: 'Never tofu.',
    retiredAt: null,
    revision: 2,
    createdAt: '2026-10-01T08:00:00+00:00',
    updatedAt: '2026-10-02T08:00:00+00:00',
    ...overrides,
  };
}

describe('WeeklyThemesService', () => {
  let service: WeeklyThemesService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(WeeklyThemesService);
  });

  afterEach(() => http.verify());

  it('reads the week with credentials, standing notes included', async () => {
    const reading = service.getWeek('sams-kitchen');
    const request = http.expectOne(THEMES_URL);
    expect(request.request.withCredentials).toBeTrue();
    request.flush({ themes: [wireTheme()] });

    const outcome = await reading;

    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') {
      expect(outcome.themes[0].standingNotes).toBe('Never tofu.');
      expect(outcome.themes[0].description).toBeNull();
    }
  });

  it('refuses a week with a theme it cannot read rather than dropping the theme', async () => {
    // This client replaces the week with what it shows, so a theme missing from the read would be retired
    // by the next save.
    const reading = service.getWeek('sams-kitchen');
    http.expectOne(THEMES_URL).flush({ themes: [wireTheme(), wireTheme({ day: 'Someday', key: 'x' })] });

    expect((await reading).status).toBe('unavailable');
  });

  it('tells a missing workspace apart from a failed read', async () => {
    const missing = service.getWeek('sams-kitchen');
    http.expectOne(THEMES_URL).flush(null, { status: 404, statusText: 'Not Found' });
    expect((await missing).status).toBe('not_found');

    const failed = service.getWeek('sams-kitchen');
    http.expectOne(THEMES_URL).error(new ProgressEvent('network'));
    expect((await failed).status).toBe('unavailable');
  });

  it('replaces the whole week in one write', async () => {
    const input = {
      day: 'Monday' as const,
      key: 'meat-free-monday',
      displayName: 'Meat-free Monday',
      description: null,
      standingNotes: 'Never tofu.',
    };

    const saving = service.replaceWeek('sams-kitchen', [input]);
    const request = http.expectOne(THEMES_URL);
    expect(request.request.method).toBe('PUT');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.body).toEqual({ themes: [input] });
    request.flush({ themes: [wireTheme()] });

    expect((await saving).status).toBe('saved');
  });

  it('names each way a replace can be refused', async () => {
    const refusals: [number, string][] = [
      [403, 'forbidden'],
      [404, 'not_found'],
      [409, 'conflict'],
      [422, 'limit'],
      [500, 'unavailable'],
    ];

    for (const [status, expected] of refusals) {
      const saving = service.replaceWeek('sams-kitchen', []);
      http.expectOne(THEMES_URL).flush({ code: 'x' }, { status, statusText: 'Refused' });
      expect((await saving).status).toBe(expected);
    }
  });

  it('returns a validation refusal’s field errors in this client’s casing', async () => {
    const saving = service.replaceWeek('sams-kitchen', []);
    http
      .expectOne(THEMES_URL)
      .flush(
        { code: 'content.weekly_themes.invalid', errors: { 'themes[0].StandingNotes': ['Too long.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

    const outcome = await saving;

    expect(outcome.status).toBe('validation_failed');
    if (outcome.status === 'validation_failed') {
      expect(outcome.fieldErrors).toEqual({ 'themes[0].standingNotes': ['Too long.'] });
    }
  });

  it('deletes one theme by key and answers with the week that remains', async () => {
    const deleting = service.deleteTheme('sams-kitchen', 'meat-free-monday');
    const request = http.expectOne(`${THEMES_URL}/meat-free-monday`);
    expect(request.request.method).toBe('DELETE');
    request.flush({ themes: [] });

    const outcome = await deleting;
    expect(outcome.status).toBe('deleted');

    const forbidden = service.deleteTheme('sams-kitchen', 'meat-free-monday');
    http.expectOne(`${THEMES_URL}/meat-free-monday`).flush(null, { status: 403, statusText: 'Forbidden' });
    expect((await forbidden).status).toBe('forbidden');
  });

  it('reads a page of history, passing the cursor and page size along', async () => {
    const reading = service.getHistory('sams-kitchen', 'meat-free-monday', { cursor: 'abc', limit: 5 });
    const request = http.expectOne((candidate) => candidate.url === `${THEMES_URL}/meat-free-monday/history`);
    expect(request.request.params.get('cursor')).toBe('abc');
    expect(request.request.params.get('limit')).toBe('5');
    request.flush({
      items: [
        {
          contextId: 'c1',
          workingTitle: null,
          day: 'Monday',
          isArchived: true,
          latestOutcomeAt: '2026-10-05T09:00:00+00:00',
          outcomes: [
            { kind: 'KeptPicture', at: '2026-10-05T09:00:00+00:00', mediaAssetId: 'a1' },
            // A kind this client does not know yet is left out, not fatal.
            { kind: 'PublishedSomewhere', at: '2026-10-06T09:00:00+00:00' },
          ],
        },
      ],
      nextCursor: 'def',
    });

    const outcome = await reading;

    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') {
      expect(outcome.page.nextCursor).toBe('def');
      expect(outcome.page.items[0].isArchived).toBeTrue();
      expect(outcome.page.items[0].outcomes.map((entry) => entry.kind)).toEqual(['KeptPicture']);
      expect(outcome.page.items[0].outcomes[0].mediaAssetId).toBe('a1');
    }
  });

  it('reports a deleted theme’s history as not found', async () => {
    const reading = service.getHistory('sams-kitchen', 'gone');
    http.expectOne(`${THEMES_URL}/gone/history`).flush(null, { status: 404, statusText: 'Not Found' });

    expect((await reading).status).toBe('not_found');
  });
});

describe('weeklyThemeKeyFor', () => {
  it('makes a lowercase, hyphenated key from a name', () => {
    expect(weeklyThemeKeyFor('  Taco Tuesday!  ', 'Tuesday', new Set())).toBe('taco-tuesday');
    expect(weeklyThemeKeyFor('Crème brûlée & co', 'Friday', new Set())).toBe('creme-brulee-co');
  });

  it('never returns a key that is taken, retired themes included', () => {
    const taken = new Set(['soup-day', 'soup-day-2']);

    expect(weeklyThemeKeyFor('Soup day', 'Monday', taken)).toBe('soup-day-3');
  });

  it('falls back to the day for a name with nothing usable in it', () => {
    expect(weeklyThemeKeyFor('🌮🌮', 'Tuesday', new Set())).toBe('tuesday-theme');
  });

  it('stays inside the server’s length and shape', () => {
    const key = weeklyThemeKeyFor('a'.repeat(200), 'Monday', new Set());

    expect(key.length).toBeLessThanOrEqual(64);
    expect(key).toMatch(/^[a-z0-9][a-z0-9-]*$/);
  });
});
