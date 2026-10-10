import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { WorkspaceSettingsService } from './workspace-settings.service';

const WORKSPACE_URL = 'https://gateway.example/api/v1/workspaces/sams-kitchen';
const PREFERENCE_URL = `${WORKSPACE_URL}/measurement-preference`;

function wire(defaultMeasurementSystem: string = 'UsCustomary') {
  return { workspaceId: 'w1', name: "Sam's Kitchen", slug: 'sams-kitchen', role: 'Owner', defaultMeasurementSystem };
}

describe('WorkspaceSettingsService', () => {
  let service: WorkspaceSettingsService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(WorkspaceSettingsService);
  });

  afterEach(() => http.verify());

  it('reads the workspace with credentials', async () => {
    const reading = service.get('sams-kitchen');
    const request = http.expectOne(WORKSPACE_URL);
    expect(request.request.withCredentials).toBeTrue();
    request.flush(wire('Metric'));

    const outcome = await reading;

    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') expect(outcome.workspace.defaultMeasurementSystem).toBe('Metric');
  });

  it('tells a missing workspace apart from a failed read', async () => {
    const missing = service.get('sams-kitchen');
    http.expectOne(WORKSPACE_URL).flush(null, { status: 404, statusText: 'Not Found' });
    expect((await missing).status).toBe('not_found');

    const failed = service.get('sams-kitchen');
    http.expectOne(WORKSPACE_URL).error(new ProgressEvent('network'));
    expect((await failed).status).toBe('unavailable');
  });

  it('reports a body it cannot read as unavailable rather than guessing a system', async () => {
    const reading = service.get('sams-kitchen');
    http.expectOne(WORKSPACE_URL).flush(wire('Imperial'));

    expect((await reading).status).toBe('unavailable');
  });

  /** The workspace is named by the route alone: nothing in the body says which one to change. */
  it('puts only the chosen system, to the route-named workspace', async () => {
    const saving = service.setMeasurementSystem('sams-kitchen', 'Metric');
    const request = http.expectOne(PREFERENCE_URL);
    expect(request.request.method).toBe('PUT');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.body).toEqual({ defaultMeasurementSystem: 'Metric' });
    request.flush(wire('Metric'));

    const outcome = await saving;

    expect(outcome.status).toBe('saved');
    if (outcome.status === 'saved') expect(outcome.workspace.defaultMeasurementSystem).toBe('Metric');
  });

  it('maps a refused save to forbidden and a failed one to unavailable', async () => {
    const refused = service.setMeasurementSystem('sams-kitchen', 'Metric');
    http.expectOne(PREFERENCE_URL).flush(null, { status: 403, statusText: 'Forbidden' });
    expect((await refused).status).toBe('forbidden');

    const failed = service.setMeasurementSystem('sams-kitchen', 'Metric');
    http.expectOne(PREFERENCE_URL).error(new ProgressEvent('network'));
    expect((await failed).status).toBe('unavailable');
  });
});
