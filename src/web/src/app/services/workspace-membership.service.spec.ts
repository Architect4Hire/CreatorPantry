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

  it('loads and decodes active memberships from the real wire format (named string role/status)', async () => {
    const loading = service.load();
    const req = http.expectOne('https://gateway.example/api/v1/me');
    expect(req.request.withCredentials).toBeTrue();
    req.flush([
      { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' },
      { workspaceId: 'w2', workspaceSlug: 'weeknight', workspaceName: 'Weeknight', membershipId: 'm2', role: 'Viewer', status: 'Active' },
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
      { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' },
      { workspaceId: 'w2', workspaceSlug: 'invited-only', workspaceName: 'Invited', membershipId: 'm2', role: 'Viewer', status: 'Invited' },
      { workspaceId: 'w3', workspaceSlug: 'removed', workspaceName: 'Removed', membershipId: 'm3', role: 'Editor', status: 'Removed' },
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

    secondReq.flush([{ workspaceId: 'w2', workspaceSlug: 'second', workspaceName: 'Second', membershipId: 'm2', role: 'Viewer', status: 'Active' }]);
    await secondCall;
    const afterSecond = service.state();
    expect(afterSecond.status === 'ready' && afterSecond.memberships[0].workspaceSlug).toBe('second');

    firstReq.flush([{ workspaceId: 'w1', workspaceSlug: 'first', workspaceName: 'First', membershipId: 'm1', role: 'Viewer', status: 'Active' }]);
    await firstCall;
    const afterFirst = service.state();
    expect(afterFirst.status === 'ready' && afterFirst.memberships[0].workspaceSlug).toBe('second');
  });

  describe('create', () => {
    it('posts the name, refreshes state() from the server, and resolves with the new slug', async () => {
      const creating = service.create('Cozy Fall Recipes');
      const createReq = http.expectOne('https://gateway.example/api/v1/workspaces');
      expect(createReq.request.body).toEqual({ name: 'Cozy Fall Recipes' });
      expect(createReq.request.withCredentials).toBeTrue();
      createReq.flush(
        { workspaceId: 'w1', name: 'Cozy Fall Recipes', slug: 'cozy-fall-recipes', createdAt: '2026-01-01T00:00:00Z', membershipId: 'm1', role: 'Owner' },
        { status: 201, statusText: 'Created' },
      );

      // create() awaits an internal reload before resolving; let that continuation run before
      // asserting on the request it issues.
      await Promise.resolve();
      http.expectOne('https://gateway.example/api/v1/me').flush([
        { workspaceId: 'w1', workspaceSlug: 'cozy-fall-recipes', workspaceName: 'Cozy Fall Recipes', membershipId: 'm1', role: 'Owner', status: 'Active' },
      ]);

      expect(await creating).toEqual({ status: 'success', workspaceSlug: 'cozy-fall-recipes' });
      expect(service.state()).toEqual({
        status: 'ready',
        memberships: [{ workspaceId: 'w1', workspaceSlug: 'cozy-fall-recipes', workspaceName: 'Cozy Fall Recipes', membershipId: 'm1', role: 'Owner', status: 'Active' }],
      });
    });

    it('reports invalid with the field error message on a 400 validation failure', async () => {
      const creating = service.create('');
      http.expectOne('https://gateway.example/api/v1/workspaces').flush(
        { errors: { name: ['Enter a workspace name.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(await creating).toEqual({ status: 'invalid', message: 'Enter a workspace name.' });
    });

    it('reports invalid with a name-conflict message on a 409', async () => {
      const creating = service.create('Taken Name');
      http.expectOne('https://gateway.example/api/v1/workspaces').flush('conflict', { status: 409, statusText: 'Conflict' });

      expect(await creating).toEqual({ status: 'invalid', message: 'That name is already taken. Try a different one.' });
    });

    it('reports unavailable when the API is unreachable', async () => {
      const creating = service.create('Cozy Fall Recipes');
      http.expectOne('https://gateway.example/api/v1/workspaces').flush('down', { status: 503, statusText: 'Service Unavailable' });

      expect(await creating).toEqual({ status: 'unavailable' });
    });
  });
});
