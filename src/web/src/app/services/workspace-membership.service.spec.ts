import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { WorkspaceMembershipService } from './workspace-membership.service';

describe('WorkspaceMembershipService', () => {
  let service: WorkspaceMembershipService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(WorkspaceMembershipService);
  });

  afterEach(() => http.verify());

  it('loads and decodes active memberships from the real wire format (numeric role/status)', async () => {
    const loading = service.load();
    const req = http.expectOne('https://gateway.example/api/v1/me');
    expect(req.request.withCredentials).toBeTrue();
    req.flush([
      { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 30, status: 10 },
      { workspaceId: 'w2', workspaceSlug: 'weeknight', workspaceName: 'Weeknight', membershipId: 'm2', role: 0, status: 10 },
    ]);
    await loading;

    expect(service.state()).toEqual({
      status: 'ready',
      memberships: [
        { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' },
        { workspaceId: 'w2', workspaceSlug: 'weeknight', workspaceName: 'Weeknight', membershipId: 'm2', role: 'Viewer', status: 'Active' },
      ],
    });
  });

  it('filters out inactive (invited/removed) memberships', async () => {
    const loading = service.load();
    http.expectOne('https://gateway.example/api/v1/me').flush([
      { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 30, status: 10 },
      { workspaceId: 'w2', workspaceSlug: 'invited-only', workspaceName: 'Invited', membershipId: 'm2', role: 0, status: 0 },
      { workspaceId: 'w3', workspaceSlug: 'removed', workspaceName: 'Removed', membershipId: 'm3', role: 20, status: 20 },
    ]);
    await loading;

    const state = service.state();
    expect(state.status).toBe('ready');
    expect(state.status === 'ready' ? state.memberships.map((m) => m.workspaceSlug) : []).toEqual(['cozy-fall']);
  });

  it('degrades to the error state when the request fails', async () => {
    const loading = service.load();
    http.expectOne('https://gateway.example/api/v1/me').flush('down', { status: 503, statusText: 'Service Unavailable' });
    await loading;

    expect(service.state()).toEqual({ status: 'error' });
  });

  it('ensureLoaded only issues one request and does not reload once ready', async () => {
    const first = service.ensureLoaded();
    http.expectOne('https://gateway.example/api/v1/me').flush([]);
    await first;
    expect(service.state()).toEqual({ status: 'ready', memberships: [] });

    await service.ensureLoaded();
    http.expectNone('https://gateway.example/api/v1/me');
  });

  it('retries on the next ensureLoaded() call after landing in error, instead of caching the failure forever', async () => {
    const first = service.ensureLoaded();
    http.expectOne('https://gateway.example/api/v1/me').flush('down', { status: 503, statusText: 'Service Unavailable' });
    await first;
    expect(service.state()).toEqual({ status: 'error' });

    const second = service.ensureLoaded();
    http.expectOne('https://gateway.example/api/v1/me').flush([]);
    await second;

    expect(service.state()).toEqual({ status: 'ready', memberships: [] });
  });

  it('lets the most recently started load() call win, even if its response resolves before an earlier, now-stale call\'s', async () => {
    const firstCall = service.load();
    const firstReq = http.expectOne('https://gateway.example/api/v1/me');

    const secondCall = service.load();
    const secondReq = http.expectOne('https://gateway.example/api/v1/me');

    secondReq.flush([{ workspaceId: 'w2', workspaceSlug: 'second', workspaceName: 'Second', membershipId: 'm2', role: 0, status: 10 }]);
    await secondCall;
    const afterSecond = service.state();
    expect(afterSecond.status === 'ready' && afterSecond.memberships[0].workspaceSlug).toBe('second');

    firstReq.flush([{ workspaceId: 'w1', workspaceSlug: 'first', workspaceName: 'First', membershipId: 'm1', role: 0, status: 10 }]);
    await firstCall;
    const afterFirst = service.state();
    expect(afterFirst.status === 'ready' && afterFirst.memberships[0].workspaceSlug).toBe('second');
  });
});
