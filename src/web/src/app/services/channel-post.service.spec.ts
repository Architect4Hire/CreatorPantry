import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { ChannelPostService } from './channel-post.service';

const WORKSPACE = 'https://gateway.example/api/v1/workspaces/cozy-fall';
const REQUESTS = `${WORKSPACE}/channel-post-requests`;
const POSTS = `${WORKSPACE}/creative-contexts/ctx-1/posts`;

function operation(status = 'Requested'): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status,
    taskType: 'ChannelPosts',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-10T12:00:00Z',
    statusChangedAt: '2026-10-10T12:00:00Z',
    failureCategory: null,
    proposal: null,
  };
}

function revision(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'rev-1',
    revisionNumber: 1,
    parentRevisionId: null,
    source: 'AiGenerated',
    body: 'Olive oil cake, still warm.',
    characterCount: 26,
    characterLimit: 2200,
    limitStatus: 'Within',
    channelProfileVersion: '1.0.0',
    aiProposalId: 'p-1',
    promptTemplateId: 'content.channel-posts',
    promptTemplateVersion: '1.0.0',
    recipeId: null,
    recipeVersionId: null,
    brandProfileRevisionId: null,
    brandStyleGuideVersionId: null,
    createdAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function wirePackage(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'pkg-1',
    creativeContextId: 'ctx-1',
    channels: [
      {
        channelKey: 'instagram',
        status: 'Proposed',
        latest: revision(),
        accepted: null,
        isCurrent: null,
        staleSince: null,
        staleReasons: 'None',
        updatedAt: '2026-10-10T12:00:00Z',
      },
    ],
    createdAt: '2026-10-10T12:00:00Z',
    updatedAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

/** A ProblemDetails body, as `ProblemResults` writes one. */
function problem(code: string, title = 'No.'): Record<string, unknown> {
  return { code, title };
}

describe('ChannelPostService', () => {
  let service: ChannelPostService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(ChannelPostService);
  });

  afterEach(() => http.verify());

  // ---- asking ------------------------------------------------------------------------------------------

  it('posts the piece of work and the channels in the order they were named, with the key', async () => {
    const pending = service.request(
      'cozy-fall',
      { creativeContextId: 'ctx-1', channelKeys: ['pinterest', 'instagram'] },
      'key-1',
    );

    const request = http.expectOne(REQUESTS);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      creativeContextId: 'ctx-1',
      channelKeys: ['pinterest', 'instagram'],
    });
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.withCredentials).toBeTrue();

    request.flush(operation());
    const outcome = await pending;

    expect(outcome.status).toBe('accepted');
    expect(outcome.status === 'accepted' && outcome.replayed).toBeFalse();
  });

  it('reads a replay off the header, so a retry is not reported as a second set of posts', async () => {
    const pending = service.request('cozy-fall', { creativeContextId: 'ctx-1', channelKeys: ['x'] }, 'key-1');

    http.expectOne(REQUESTS).flush(operation(), { headers: { 'Idempotent-Replayed': 'true' } });
    const outcome = await pending;

    expect(outcome.status === 'accepted' && outcome.replayed).toBeTrue();
  });

  /**
   * Each refusal has its own outcome because each has its own remedy.
   *
   * The spent-allowance body carries the four figures USAGE-007 requires, because without `resetsAt` the
   * refusal cannot say when it lifts and is deliberately not reported as one.
   */
  const refusals: readonly {
    readonly name: string;
    readonly status: number;
    readonly code: string;
    readonly expected: string;
    readonly extensions?: Record<string, unknown>;
  }[] = [
    { name: 'a task switched off for the deployment', status: 400, code: 'ai.channelPosts.not_enabled', expected: 'task_not_enabled' },
    { name: 'a piece of work this workspace does not have', status: 404, code: 'ai.channelPostsContext.not_found', expected: 'refused' },
    { name: 'a retired channel with no post yet', status: 422, code: 'ai.channelPosts.channel.unprocessable', expected: 'refused' },
    { name: 'a key reused for a different request', status: 422, code: 'idempotency.key_reused', expected: 'idempotency_key_conflict' },
    {
      name: 'a spent allowance',
      status: 429,
      code: 'ai.quota.exhausted',
      expected: 'quota_exhausted',
      extensions: { unit: 'Credits', allowance: 1000, remaining: 0, required: 20, resetsAt: '2026-11-01T00:00:00Z' },
    },
    { name: 'an account with AI switched off', status: 403, code: 'ai.quota.suspended', expected: 'account_suspended' },
    { name: 'a role that may not write posts', status: 403, code: 'workspace.forbidden', expected: 'forbidden' },
  ];

  for (const refusal of refusals) {
    it(`reports ${refusal.name} as ${refusal.expected}`, async () => {
      const pending = service.request('cozy-fall', { creativeContextId: 'ctx-1', channelKeys: ['x'] }, 'key-1');

      http.expectOne(REQUESTS).flush(
        { ...problem(refusal.code), ...(refusal.extensions ?? {}) },
        { status: refusal.status, statusText: 'No' },
      );

      expect((await pending).status).toBe(refusal.expected);
    });
  }

  // ---- following ---------------------------------------------------------------------------------------

  it('watches the request and reads only its status half, which is what a tracker follows', async () => {
    const pending = firstValueFrom(service.watch('cozy-fall', 'r-1'));

    const request = http.expectOne(`${REQUESTS}/r-1`);
    expect(request.request.method).toBe('GET');
    request.flush({ request: operation('Proposed'), package: wirePackage() });

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    expect(outcome.status === 'found' && outcome.operation.status).toBe('Proposed');
  });

  it('never throws out of a poll, so one failed reading cannot kill a loop', async () => {
    const pending = firstValueFrom(service.watch('cozy-fall', 'r-1'));

    http.expectOne(`${REQUESTS}/r-1`).flush('', { status: 500, statusText: 'Server Error' });

    expect((await pending).status).toBe('unavailable');
  });

  // ---- reading the posts -------------------------------------------------------------------------------

  it('reads the posts of one piece of work', async () => {
    const pending = service.readPackage('cozy-fall', 'ctx-1');

    const request = http.expectOne(POSTS);
    expect(request.request.method).toBe('GET');
    request.flush(wirePackage());

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    expect(outcome.status === 'found' && outcome.package?.channels.length).toBe(1);
  });

  it('reads a piece of work with nothing written for it as found and empty, not as missing', async () => {
    const pending = service.readPackage('cozy-fall', 'ctx-1');

    http.expectOne(POSTS).flush(null, { status: 204, statusText: 'No Content' });

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    expect(outcome.status === 'found' && outcome.package).toBeNull();
  });

  it('reads a piece of work in another workspace as not found', async () => {
    const pending = service.readPackage('cozy-fall', 'ctx-1');

    http.expectOne(POSTS).flush(problem('content.social.not_found'), { status: 404, statusText: 'No' });

    expect((await pending).status).toBe('not_found');
  });

  // ---- editing -----------------------------------------------------------------------------------------

  it('patches the words and the revision they were written against, and nothing else', async () => {
    const pending = service.edit('cozy-fall', 'ctx-1', 'instagram', {
      body: 'My own words.',
      expectedLatestRevisionId: 'rev-1',
    });

    const request = http.expectOne(`${POSTS}/instagram`);
    expect(request.request.method).toBe('PATCH');

    // No count, no limit and no status: the server measures the body against the channel's own profile.
    expect(request.request.body).toEqual({ body: 'My own words.', expectedLatestRevisionId: 'rev-1' });

    request.flush(wirePackage());
    const outcome = await pending;

    expect(outcome.status).toBe('saved');
  });

  it('reports a post that moved on as stale, which is the one refusal with its own remedy', async () => {
    const pending = service.edit('cozy-fall', 'ctx-1', 'instagram', {
      body: 'My own words.',
      expectedLatestRevisionId: 'rev-old',
    });

    http.expectOne(`${POSTS}/instagram`).flush(problem('content.social.stale.conflict'), {
      status: 409,
      statusText: 'Conflict',
    });

    expect((await pending).status).toBe('stale');
  });

  it('carries the server’s own sentence for a refusal only it can explain', async () => {
    const pending = service.edit('cozy-fall', 'ctx-1', 'instagram', {
      body: '',
      expectedLatestRevisionId: null,
    });

    http.expectOne(`${POSTS}/instagram`).flush(problem('content.social.invalid', 'A post needs some words.'), {
      status: 400,
      statusText: 'Bad Request',
    });

    const outcome = await pending;
    expect(outcome.status).toBe('refused');
    expect(outcome.status === 'refused' && outcome.message).toBe('A post needs some words.');
  });

  // ---- deciding ----------------------------------------------------------------------------------------

  it('posts one decision about one revision', async () => {
    const pending = service.decide('cozy-fall', 'ctx-1', 'instagram', {
      decision: 'Accept',
      revisionId: 'rev-1',
    });

    const request = http.expectOne(`${POSTS}/instagram/disposition`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ decision: 'Accept', revisionId: 'rev-1' });

    request.flush(wirePackage({ channels: [] }));

    expect((await pending).status).toBe('saved');
  });

  it('tells the three conflicts apart by their codes rather than by the 409 they share', async () => {
    const codes = [
      { code: 'content.social.decision.conflict', expected: 'decision_conflict' },
      { code: 'content.social.source_stale.conflict', expected: 'source_stale' },
      { code: 'content.social.stale.conflict', expected: 'stale' },
    ];

    for (const { code, expected } of codes) {
      const pending = service.decide('cozy-fall', 'ctx-1', 'instagram', {
        decision: 'Accept',
        revisionId: 'rev-1',
      });

      http.expectOne(`${POSTS}/instagram/disposition`).flush(problem(code, 'The server’s own words.'), {
        status: 409,
        statusText: 'Conflict',
      });

      expect((await pending).status).toBe(expected);
    }
  });

  it('escapes the channel key in the path, so a key is a segment rather than a route', async () => {
    const pending = service.decide('cozy-fall', 'ctx-1', 'a/b', { decision: 'Reject', revisionId: 'rev-1' });

    http.expectOne(`${POSTS}/a%2Fb/disposition`).flush(wirePackage());

    expect((await pending).status).toBe('saved');
  });
});
