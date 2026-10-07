import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { ContentSeedService } from './content-seed.service';

const URL = 'https://gateway.example/api/v1/workspaces/cozy-fall/content-seeds';

function facet(key: string, displayName: string, pinned = false): Record<string, unknown> {
  return { key, displayName, pinned };
}

function seedPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    token: 'abc-123',
    cuisine: facet('thai', 'Thai'),
    dishType: facet('main-course', 'Main course'),
    method: { ...facet('stir-fry', 'Stir-fry'), requiresSafetyCaution: false },
    photographyStyle: facet('overhead-flat-lay', 'Overhead flat lay'),
    channel: facet('instagram', 'Instagram'),
    day: { day: 'Wednesday', pinned: false, theme: null },
    occasion: facet('weeknight', 'Weeknight'),
    description: 'Develop a Thai main course using the stir-fry method.',
    ...overrides,
  };
}

describe('ContentSeedService', () => {
  let service: ContentSeedService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(ContentSeedService);
  });

  afterEach(() => http.verify());

  it('reads one seed with a GET and nothing else, because the generator keeps no record', async () => {
    const pending = firstValueFrom(service.generate('cozy-fall'));

    const request = http.expectOne(URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.params.keys()).toEqual([]);

    request.flush(seedPayload());

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') expect(outcome.seed.token).toBe('abc-123');
  });

  it('sends every pin under the name the server binds it by', async () => {
    const pending = firstValueFrom(
      service.generate('cozy-fall', {
        token: 'spring-bakes',
        channel: 'instagram',
        day: 'Friday',
        cuisine: 'thai',
        method: 'stir-fry',
      }),
    );

    const request = http.expectOne(
      (candidate) => candidate.url === URL && candidate.params.get('PhotographyStyle') === null,
    );
    expect(request.request.params.get('Token')).toBe('spring-bakes');
    expect(request.request.params.get('Channel')).toBe('instagram');
    expect(request.request.params.get('Day')).toBe('Friday');
    expect(request.request.params.get('Cuisine')).toBe('thai');
    expect(request.request.params.get('Method')).toBe('stir-fry');

    request.flush(seedPayload());
    await pending;
  });

  it('escapes the workspace segment rather than letting it shape the path', async () => {
    const pending = firstValueFrom(service.generate('cozy fall/../other'));

    const request = http.expectOne(
      'https://gateway.example/api/v1/workspaces/cozy%20fall%2F..%2Fother/content-seeds',
    );
    request.flush(seedPayload());

    await pending;
  });

  it('reports a refused pin as refused, with the field the server named', async () => {
    const pending = firstValueFrom(service.generate('cozy-fall', { cuisine: 'atlantean' }));

    http.expectOne((candidate) => candidate.url === URL).flush(
      {
        code: 'content.seed.invalid',
        title: 'That idea cannot be put together.',
        errors: { Cuisine: ['That cuisine is no longer available.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await pending;
    expect(outcome.status).toBe('refused');
    if (outcome.status === 'refused') {
      expect(outcome.fieldErrors['Cuisine']).toEqual(['That cuisine is no longer available.']);
    }
  });

  it('does not read some other 400 as a refused pin', async () => {
    const pending = firstValueFrom(service.generate('cozy-fall'));

    http.expectOne(URL).flush({ code: 'something.else', title: 'No' }, { status: 400, statusText: 'Bad Request' });

    expect((await pending).status).toBe('unavailable');
  });

  it('reports a workspace it cannot see as unavailable, without a word about why', async () => {
    const pending = firstValueFrom(service.generate('someone-elses'));

    http
      .expectOne('https://gateway.example/api/v1/workspaces/someone-elses/content-seeds')
      .flush({ code: 'workspace.not_found' }, { status: 404, statusText: 'Not Found' });

    expect((await pending).status).toBe('unavailable');
  });

  it('reports an outage as unavailable', async () => {
    const pending = firstValueFrom(service.generate('cozy-fall'));

    http.expectOne(URL).error(new ProgressEvent('network error'));

    expect((await pending).status).toBe('unavailable');
  });

  it('reports a body it cannot read as unavailable rather than handing back half a seed', async () => {
    const pending = firstValueFrom(service.generate('cozy-fall'));

    http.expectOne(URL).flush(seedPayload({ day: { day: 'Someday', pinned: false, theme: null } }));

    expect((await pending).status).toBe('unavailable');
  });

  it('aborts the request when the caller unsubscribes, so a second ask cannot lose to the first', () => {
    const subscription = service.generate('cozy-fall').subscribe();
    const request = http.expectOne(URL);

    subscription.unsubscribe();

    expect(request.cancelled).toBeTrue();
  });
});

describe('ContentSeedService before the gateway address is known', () => {
  it('answers unavailable without making a request', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const outcome = await firstValueFrom(TestBed.inject(ContentSeedService).generate('cozy-fall'));

    expect(outcome.status).toBe('unavailable');
    http.verify();
  });
});
