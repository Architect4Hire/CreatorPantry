import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { dashboardBody } from '../features/dashboard/dashboard.fixtures';
import { decodeDashboard } from '../models/dashboard.models';
import { DashboardService } from './dashboard.service';

const GATEWAY = 'https://gateway.example';
const URL = `${GATEWAY}/api/v1/workspaces/cozy-fall/dashboard`;

describe('DashboardService', () => {
  let service: DashboardService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(DashboardService);
  });

  afterEach(() => http.verify());

  it('reads the workspace’s dashboard from its own route, with credentials', async () => {
    const pending = firstValueFrom(service.get('cozy-fall'));

    const request = http.expectOne(URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBe(true);
    request.flush(dashboardBody());

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    if (outcome.status !== 'found') return;
    expect(outcome.dashboard.timeZoneId).toBe('America/Chicago');
    expect(outcome.dashboard.board).toEqual({
      status: 'ready',
      data: {
        total: 12,
        columns: [
          { column: 'ToDo', count: 5 },
          { column: 'InProcess', count: 4 },
          { column: 'ReadyForReview', count: 2 },
          { column: 'Approved', count: 1 },
        ],
      },
    });
  });

  it('gives one answer for a workspace that is unknown or not this person’s', async () => {
    const pending = firstValueFrom(service.get('cozy-fall'));
    http.expectOne(URL).flush({ code: 'workspace.not_found' }, { status: 404, statusText: 'Not Found' });

    expect(await pending).toEqual({ status: 'not_found' });
  });

  it('is unavailable when the request fails or the envelope cannot be read', async () => {
    const failed = firstValueFrom(service.get('cozy-fall'));
    http.expectOne(URL).flush(null, { status: 503, statusText: 'Unavailable' });
    expect(await failed).toEqual({ status: 'unavailable' });

    const unreadable = firstValueFrom(service.get('cozy-fall'));
    http.expectOne(URL).flush({ generatedAt: 7 });
    expect(await unreadable).toEqual({ status: 'unavailable' });
  });
});

describe('decodeDashboard', () => {
  it('reads every section the route sends, as the route sends it', () => {
    const dashboard = decodeDashboard(dashboardBody())!;

    expect(dashboard.today).toBe('2026-10-14');
    expect(dashboard.recipesInProgress.status).toBe('ready');
    expect(dashboard.readinessBlockers.status).toBe('ready');
    expect(dashboard.aiProposals.status).toBe('ready');
    expect(dashboard.testFollowUps.status).toBe('ready');
    expect(dashboard.upcoming.status).toBe('ready');
    expect(dashboard.recentUploads.status).toBe('ready');
    expect(dashboard.publishing.status).toBe('ready');
  });

  it('keeps a section the server could not read as unavailable, and the rest as they are', () => {
    const dashboard = decodeDashboard(dashboardBody({ testFollowUps: { status: 'Unavailable', data: null } }))!;

    expect(dashboard.testFollowUps).toEqual({ status: 'unavailable' });
    expect(dashboard.board.status).toBe('ready');
  });

  it('costs one card, not the page, for a section it cannot read', () => {
    const dashboard = decodeDashboard(
      dashboardBody({
        board: { status: 'Ready', data: { total: 3, columns: [{ column: 'Somewhere new', count: 3 }] } },
        recentUploads: { status: 'Ready', data: { items: [{ id: 'a1', title: 7, createdAt: 'x' }] } },
      }),
    )!;

    expect(dashboard.board).toEqual({ status: 'unavailable' });
    expect(dashboard.recentUploads).toEqual({ status: 'unavailable' });
    expect(dashboard.recipesInProgress.status).toBe('ready');
  });

  it('says a publishing state it does not know is unrecognised rather than dropping the post', () => {
    const body = dashboardBody();
    const publishing = (body['publishing'] as { data: { needsAttention: { status: string }[] } }).data;
    publishing.needsAttention[0].status = 'SomethingNew';

    const dashboard = decodeDashboard(body)!;

    expect(dashboard.publishing.status).toBe('ready');
    if (dashboard.publishing.status !== 'ready') return;
    expect(dashboard.publishing.data.needsAttention[0].status).toBe('Unrecognised');
  });
});
