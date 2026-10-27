import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { AiFeaturesService } from './ai-features.service';

const URL = 'https://gateway.example/api/v1/workspaces/sams-kitchen/ai-features';

function wire(enabled: readonly string[] = []) {
  return {
    features: ['recipe.revision', 'recipe.review'].map((task) => ({
      task,
      enabled: enabled.includes(task),
      setByWorkspace: enabled.length > 0,
    })),
  };
}

describe('AiFeaturesService', () => {
  let service: AiFeaturesService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(AiFeaturesService);
  });

  afterEach(() => http.verify());

  it('reads the features with credentials', async () => {
    const reading = service.get('sams-kitchen');
    const request = http.expectOne(URL);
    expect(request.request.withCredentials).toBeTrue();
    request.flush(wire(['recipe.review']));

    const outcome = await reading;

    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') {
      expect(outcome.features.map((feature) => [feature.task, feature.enabled])).toEqual([
        ['recipe.revision', false],
        ['recipe.review', true],
      ]);
    }
  });

  it('tells a missing workspace apart from a failed read', async () => {
    const missing = service.get('sams-kitchen');
    http.expectOne(URL).flush(null, { status: 404, statusText: 'Not Found' });
    expect((await missing).status).toBe('not_found');

    const failed = service.get('sams-kitchen');
    http.expectOne(URL).error(new ProgressEvent('network'));
    expect((await failed).status).toBe('unavailable');
  });

  it('reports a body it cannot read as unavailable rather than showing part of it', async () => {
    const reading = service.get('sams-kitchen');
    http.expectOne(URL).flush({ features: [{ task: 'recipe.revision', enabled: 'yes', setByWorkspace: false }] });

    expect((await reading).status).toBe('unavailable');
  });

  /** The workspace is named by the route alone: nothing in the body says which one to change. */
  it('puts only the enabled set, to the route-named workspace', async () => {
    const saving = service.replace('sams-kitchen', ['recipe.revision']);
    const request = http.expectOne(URL);
    expect(request.request.method).toBe('PUT');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.body).toEqual({ enabled: ['recipe.revision'] });
    request.flush(wire(['recipe.revision']));

    const outcome = await saving;

    expect(outcome.status).toBe('saved');
    if (outcome.status === 'saved') expect(outcome.features[0].enabled).toBeTrue();
  });

  it('maps each refusal to its own outcome', async () => {
    for (const [status, expected] of [
      [403, 'forbidden'],
      [404, 'not_found'],
      [409, 'conflict'],
      [500, 'unavailable'],
    ] as const) {
      const saving = service.replace('sams-kitchen', []);
      http.expectOne(URL).flush(null, { status, statusText: 'x' });
      expect((await saving).status)
        .withContext(String(status))
        .toBe(expected);
    }
  });
});
