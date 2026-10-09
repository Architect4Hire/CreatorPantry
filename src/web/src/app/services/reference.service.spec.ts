import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { ReferenceService } from './reference.service';

const UNITS_URL = 'https://gateway.example/api/v1/reference/units';
const INGREDIENTS_URL = 'https://gateway.example/api/v1/reference/ingredients';
const CUISINES_URL = 'https://gateway.example/api/v1/reference/cuisines';
const COURSES_URL = 'https://gateway.example/api/v1/reference/courses';
const TECHNIQUES_URL = 'https://gateway.example/api/v1/reference/techniques';

function entry(id: string, code: string, displayName: string): Record<string, unknown> {
  return { id, code, displayName };
}

function technique(id: string, code: string, requiresSafetyCaution = false): Record<string, unknown> {
  return { id, code, displayName: code, requiresSafetyCaution };
}

function ingredient(id: string, canonicalName: string, aliases: readonly string[] = []): Record<string, unknown> {
  return { id, canonicalName, foodCategoryCode: null, defaultCountUnitCode: null, aliases };
}

function unit(id: string, code: string): Record<string, unknown> {
  return {
    id,
    code,
    displayName: code,
    pluralName: `${code}s`,
    abbreviation: code.slice(0, 2),
    dimension: 'Mass',
    system: 'Metric',
    baseUnitFactor: 1,
    displayPrecision: 2,
  };
}

/**
 * Lets the pending page settle before the next one is expected. The cursor loop awaits each response before
 * it issues the following request, so a second page does not exist until the first has been read.
 */
function tick(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

describe('ReferenceService', () => {
  let service: ReferenceService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(ReferenceService);
  });

  afterEach(() => http.verify());

  it('GETs the catalogue with credentials and the largest page the server serves', async () => {
    const call = service.listUnits();
    const req = http.expectOne((request) => request.url === UNITS_URL);

    expect(req.request.method).toBe('GET');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.params.get('limit')).toBe('100');
    expect(req.request.params.get('cursor')).toBeNull();

    req.flush({ items: [unit('u1', 'gram')], nextCursor: null });

    const outcome = await call;
    expect(outcome.status).toBe('found');
    expect(outcome.status === 'found' && outcome.units.length).toBe(1);
  });

  // The catalogue is cursor-paged, so a client that reads one page is a client that quietly hides units.
  it('follows the cursor until the catalogue is exhausted', async () => {
    const call = service.listUnits();

    http.expectOne((request) => request.url === UNITS_URL && request.params.get('cursor') === null)
      .flush({ items: [unit('u1', 'gram')], nextCursor: 'page-2' });
    await tick();

    const second = http.expectOne((request) => request.url === UNITS_URL && request.params.get('cursor') === 'page-2');
    second.flush({ items: [unit('u2', 'kilogram')], nextCursor: null });

    const outcome = await call;
    expect(outcome.status === 'found' && outcome.units.map((each) => each.id)).toEqual(['u1', 'u2']);
  });

  // Answering `found` with half a catalogue would be worse than answering nothing: a unit that is missing
  // reads as a unit that does not exist.
  it('answers unavailable rather than returning a partial catalogue when a later page fails', async () => {
    const call = service.listUnits();

    http.expectOne((request) => request.params.get('cursor') === null)
      .flush({ items: [unit('u1', 'gram')], nextCursor: 'page-2' });
    await tick();

    http.expectOne((request) => request.params.get('cursor') === 'page-2')
      .flush({}, { status: 500, statusText: 'Server Error' });

    expect(await call).toEqual({ status: 'unavailable' });
  });

  it('answers unavailable when a page cannot be decoded', async () => {
    const call = service.listUnits();
    http.expectOne((request) => request.url === UNITS_URL).flush({ items: [{ id: 'u1' }], nextCursor: null });

    expect(await call).toEqual({ status: 'unavailable' });
  });

  it('answers unavailable on a refusal', async () => {
    const call = service.listUnits();
    http.expectOne((request) => request.url === UNITS_URL).flush({}, { status: 403, statusText: 'Forbidden' });

    expect(await call).toEqual({ status: 'unavailable' });
  });

  // Two sections of one tab ask at the same time; the catalogue is global and immutable, so they share one.
  it('makes one request for callers that overlap, and reuses the answer afterwards', async () => {
    const first = service.listUnits();
    const second = service.listUnits();

    http.expectOne((request) => request.url === UNITS_URL).flush({ items: [unit('u1', 'gram')], nextCursor: null });

    expect((await first).status).toBe('found');
    expect((await second).status).toBe('found');

    const third = await service.listUnits();
    expect(third.status).toBe('found');
    http.expectNone((request) => request.url === UNITS_URL);
  });


  // The three vocabularies that describe a dish. Global routes: nothing about the request may name a
  // workspace, because reference data has no WorkspaceId to filter by (tenancy.md).
  it('reads each dish vocabulary from its own global route', async () => {
    const cuisines = service.listCuisines();
    const cuisineReq = http.expectOne((request) => request.url === CUISINES_URL);
    expect(cuisineReq.request.withCredentials).toBeTrue();
    expect(cuisineReq.request.url).not.toContain('workspace');
    cuisineReq.flush({ items: [entry('c1', 'levantine', 'Levantine')], nextCursor: null });

    const courses = service.listCourses();
    http.expectOne((request) => request.url === COURSES_URL)
      .flush({ items: [entry('k1', 'salad', 'Salad')], nextCursor: null });

    const techniques = service.listTechniques();
    http.expectOne((request) => request.url === TECHNIQUES_URL)
      .flush({ items: [technique('t1', 'grill')], nextCursor: null });

    expect(await cuisines).toEqual({ status: 'found', entries: [{ id: 'c1', code: 'levantine', displayName: 'Levantine' }] });
    expect(await courses).toEqual({ status: 'found', entries: [{ id: 'k1', code: 'salad', displayName: 'Salad' }] });
    expect(await techniques).toEqual({
      status: 'found',
      techniques: [{ id: 't1', code: 'grill', displayName: 'grill', requiresSafetyCaution: false }],
    });
  });

  // The flag decides whether a surface showing this method must carry a caution, so dropping it silently
  // would turn "no caution attached" into "no caution needed" (.claude/rules/ai.md).
  it('carries each technique’s caution flag through', async () => {
    const call = service.listTechniques();
    http.expectOne((request) => request.url === TECHNIQUES_URL).flush({
      items: [technique('t1', 'pressure-canning', true), technique('t2', 'grill')],
      nextCursor: null,
    });

    const outcome = await call;
    expect(outcome.status === 'found' && outcome.techniques.map((each) => each.requiresSafetyCaution)).toEqual([
      true,
      false,
    ]);
  });

  it('follows the cursor and caches each vocabulary for the session', async () => {
    const call = service.listCuisines();

    http.expectOne((request) => request.url === CUISINES_URL && request.params.get('cursor') === null)
      .flush({ items: [entry('c1', 'levantine', 'Levantine')], nextCursor: 'page-2' });
    await tick();

    http.expectOne((request) => request.url === CUISINES_URL && request.params.get('cursor') === 'page-2')
      .flush({ items: [entry('c2', 'thai', 'Thai')], nextCursor: null });

    const outcome = await call;
    expect(outcome.status === 'found' && outcome.entries.map((each) => each.code)).toEqual(['levantine', 'thai']);

    expect((await service.listCuisines()).status).toBe('found');
    http.expectNone((request) => request.url === CUISINES_URL);
  });

  // A failure is not cached: the next caller tries again rather than inheriting an outage.
  it('answers unavailable on a failed vocabulary read and retries afterwards', async () => {
    const failed = service.listCourses();
    http.expectOne((request) => request.url === COURSES_URL).flush({}, { status: 500, statusText: 'Server Error' });
    expect(await failed).toEqual({ status: 'unavailable' });

    const retried = service.listCourses();
    http.expectOne((request) => request.url === COURSES_URL)
      .flush({ items: [entry('k1', 'salad', 'Salad')], nextCursor: null });
    expect((await retried).status).toBe('found');
  });

  it('answers unavailable when a vocabulary page cannot be decoded', async () => {
    const call = service.listCuisines();
    http.expectOne((request) => request.url === CUISINES_URL).flush({ items: [{ id: 'c1' }], nextCursor: null });

    expect(await call).toEqual({ status: 'unavailable' });
  });

  // The ingredient catalogue is far too large to hold client-side, so this is a search: one page, and typing
  // more is how the creator narrows it. Nothing about the request may name a workspace — reference data is
  // global (tenancy.md), and these routes sit outside /workspaces/{workspaceSlug}.
  it('searches ingredients on the global route, with credentials and one page', async () => {
    const call = firstValueFrom(service.searchIngredients('scall'));
    const req = http.expectOne((request) => request.url === INGREDIENTS_URL);

    expect(req.request.method).toBe('GET');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.params.get('search')).toBe('scall');
    expect(req.request.params.get('limit')).toBe('20');
    expect(req.request.url).not.toContain('workspace');
    expect(req.request.params.has('cursor')).toBeFalse();

    req.flush({ items: [ingredient('i1', 'Green onion', ['scallion', 'spring onion'])], nextCursor: null });

    expect(await call).toEqual({
      status: 'found',
      ingredients: [
        { id: 'i1', canonicalName: 'Green onion', foodCategoryCode: null, defaultCountUnitCode: null, aliases: ['scallion', 'spring onion'] },
      ],
      hasMore: false,
    });
  });

  // A cursor coming back means the catalogue has more than this page's worth. Reported rather than followed,
  // so the caller can say the list is the first of several instead of presenting it as all of them.
  it('reports a truncated result rather than paging through it', async () => {
    const call = firstValueFrom(service.searchIngredients('on'));
    http.expectOne((request) => request.url === INGREDIENTS_URL).flush({ items: [ingredient('i1', 'Onion')], nextCursor: 'page-2' });

    const outcome = await call;
    expect(outcome.status === 'found' && outcome.hasMore).toBeTrue();
    http.expectNone((request) => request.url === INGREDIENTS_URL);
  });

  // The server ignores a term this short and answers with the first page of everything, which would read as
  // "these are your matches". Saying so is a different answer from both "no matches" and "search is down".
  it('answers tooShort without a request for a term the server would ignore', async () => {
    expect(await firstValueFrom(service.searchIngredients('s'))).toEqual({ status: 'tooShort' });
    expect(await firstValueFrom(service.searchIngredients('  '))).toEqual({ status: 'tooShort' });

    http.expectNone((request) => request.url === INGREDIENTS_URL);
  });

  // Above ReferencePolicy.SearchMaxLength the server refuses outright; a search on the front of a pasted
  // paragraph is more use to the creator than an error.
  it('cuts an over-long term to the length the server accepts', async () => {
    const call = firstValueFrom(service.searchIngredients('a'.repeat(200)));
    const req = http.expectOne((request) => request.url === INGREDIENTS_URL);

    expect(req.request.params.get('search')).toBe('a'.repeat(128));
    req.flush({ items: [], nextCursor: null });
    await call;
  });

  it('answers unavailable on a refusal and on a shape it cannot read', async () => {
    const refused = firstValueFrom(service.searchIngredients('flour'));
    http.expectOne((request) => request.url === INGREDIENTS_URL).flush({}, { status: 503, statusText: 'Unavailable' });
    expect(await refused).toEqual({ status: 'unavailable' });

    const undecodable = firstValueFrom(service.searchIngredients('flour'));
    http.expectOne((request) => request.url === INGREDIENTS_URL).flush({ items: [{ id: 'i1' }], nextCursor: null });
    expect(await undecodable).toEqual({ status: 'unavailable' });
  });

  // The whole reason this returns an observable rather than a promise: a type-ahead has to be able to abandon
  // the answer to a term the creator has already typed past.
  it('cancels the request when the caller unsubscribes', () => {
    const subscription = service.searchIngredients('flo').subscribe();
    const req = http.expectOne((request) => request.url === INGREDIENTS_URL);

    expect(req.cancelled).toBeFalse();
    subscription.unsubscribe();
    expect(req.cancelled).toBeTrue();
  });

  // An outage is not something to inherit for the rest of the session.
  it('retries after a failure rather than caching it', async () => {
    const first = service.listUnits();
    http.expectOne((request) => request.url === UNITS_URL).flush({}, { status: 500, statusText: 'Server Error' });
    expect(await first).toEqual({ status: 'unavailable' });

    const second = service.listUnits();
    http.expectOne((request) => request.url === UNITS_URL).flush({ items: [unit('u1', 'gram')], nextCursor: null });
    expect((await second).status).toBe('found');
  });
});

describe('ReferenceService without a resolved gateway', () => {
  it('answers unavailable rather than calling a relative URL', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(ReferenceService);

    expect(await service.listUnits()).toEqual({ status: 'unavailable' });

    http.verify();
  });
});
