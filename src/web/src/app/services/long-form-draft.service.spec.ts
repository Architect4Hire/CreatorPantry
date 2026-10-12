import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { LongFormDraftService } from './long-form-draft.service';

const DRAFTS = 'https://gateway.example/api/v1/workspaces/cozy-fall/long-form-drafts';
const DRAFT = `${DRAFTS}/d-1`;

function operation(status = 'Requested'): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status,
    taskType: 'LongFormOutline',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-12T12:00:00Z',
    statusChangedAt: '2026-10-12T12:00:00Z',
    failureCategory: null,
    proposal: null,
  };
}

function wireSection(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 's-1',
    role: 'Hook',
    heading: 'Why this cake',
    intent: 'Open on the smell of it.',
    isWritten: false,
    settled: false,
    sourceReferenceId: null,
    ...overrides,
  };
}

function wireDraft(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'd-1',
    creativeContextId: 'ctx-1',
    kind: 'BlogArticle',
    status: 'Draft',
    concurrencyToken: 'AAAAAAAAB9E=',
    createdAt: '2026-10-12T12:00:00Z',
    updatedAt: '2026-10-12T12:00:00Z',
    latest: {
      revisionNumber: 1,
      source: 'AiGenerated',
      createdAt: '2026-10-12T12:00:00Z',
      sections: [wireSection()],
    },
    planCheck: { isValid: true, policyVersion: '1.0.0', findings: [] },
    ...overrides,
  };
}

function problem(code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { title: 'The server’s own sentence.', code, ...extra };
}

describe('LongFormDraftService', () => {
  let service: LongFormDraftService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(LongFormDraftService);
  });

  afterEach(() => http.verify());

  // ---- starting ----------------------------------------------------------------------------------------

  it('starts a draft with the piece of work, the kind and the key, and no workspace in the body', async () => {
    const pending = service.start('cozy-fall', { creativeContextId: 'ctx-1', kind: 'NewsletterIssue' }, 'key-1');

    const request = http.expectOne(DRAFTS);
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.body).toEqual({ creativeContextId: 'ctx-1', kind: 'NewsletterIssue' });
    expect(JSON.stringify(request.request.body)).not.toContain('workspace');

    request.flush({ draft: wireDraft({ latest: null, planCheck: null }), request: operation() }, { status: 202, statusText: 'Accepted' });

    const outcome = await pending;
    expect(outcome.status).toBe('accepted');
    if (outcome.status !== 'accepted') return;
    expect(outcome.draft.id).toBe('d-1');
    expect(outcome.draft.latest).toBeNull();
    expect(outcome.operation.aiProposalRequestId).toBe('r-1');
    expect(outcome.replayed).toBeFalse();
  });

  it('sends the optional inputs only when they are set', async () => {
    const pending = service.start(
      'cozy-fall',
      { creativeContextId: 'ctx-1', kind: 'BlogArticle', targetWords: 1200, wantedRoles: ['Hook', 'Tips'] },
      'key-1',
    );

    const request = http.expectOne(DRAFTS);
    expect(request.request.body).toEqual({
      creativeContextId: 'ctx-1',
      kind: 'BlogArticle',
      targetWords: 1200,
      wantedRoles: ['Hook', 'Tips'],
    });
    request.flush({ draft: wireDraft(), request: operation() }, { status: 202, statusText: 'Accepted' });
    await pending;
  });

  it('reports a replayed start as replayed', async () => {
    const pending = service.start('cozy-fall', { creativeContextId: 'ctx-1', kind: 'BlogArticle' }, 'key-1');

    http.expectOne(DRAFTS).flush(
      { draft: wireDraft(), request: operation() },
      { status: 202, statusText: 'Accepted', headers: { 'Idempotent-Replayed': 'true' } },
    );

    const outcome = await pending;
    expect(outcome.status === 'accepted' && outcome.replayed).toBeTrue();
  });

  it('tells a switched-off task from a refused piece of work', async () => {
    const off = service.start('cozy-fall', { creativeContextId: 'ctx-1', kind: 'BlogArticle' }, 'key-1');
    http.expectOne(DRAFTS).flush(problem('ai.longFormOutline.not_enabled'), { status: 400, statusText: 'Bad Request' });
    expect((await off).status).toBe('task_not_enabled');

    const refused = service.start('cozy-fall', { creativeContextId: 'ctx-2', kind: 'BlogArticle' }, 'key-2');
    http
      .expectOne(DRAFTS)
      .flush(problem('content.long_form.context.unprocessable'), { status: 422, statusText: 'Unprocessable' });
    expect((await refused).status).toBe('refused');
  });

  it('is unavailable when an accepted start cannot be read', async () => {
    const pending = service.start('cozy-fall', { creativeContextId: 'ctx-1', kind: 'BlogArticle' }, 'key-1');
    http.expectOne(DRAFTS).flush({ draft: wireDraft({ kind: 'Podcast' }), request: operation() });

    expect((await pending).status).toBe('unavailable');
  });

  // ---- asking again and watching -----------------------------------------------------------------------

  it('asks for another outline on the draft’s own route', async () => {
    const pending = service.requestOutline('cozy-fall', 'd-1', {}, 'key-2');

    const request = http.expectOne(`${DRAFT}/outline-requests`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-2');
    expect(request.request.body).toEqual({});
    request.flush({ draft: wireDraft(), request: operation() }, { status: 202, statusText: 'Accepted' });

    const outcome = await pending;
    expect(outcome.status).toBe('accepted');
  });

  it('says a draft that has gone is gone, rather than reporting an outage', async () => {
    const pending = service.requestOutline('cozy-fall', 'd-1', {}, 'key-2');
    http
      .expectOne(`${DRAFT}/outline-requests`)
      .flush(problem('content.long_form.draft.not_found'), { status: 404, statusText: 'Not Found' });

    const outcome = await pending;
    expect(outcome.status).toBe('refused');
  });

  it('carries a spent allowance through as the refusal it is', async () => {
    const pending = service.requestOutline('cozy-fall', 'd-1', {}, 'key-2');
    http
      .expectOne(`${DRAFT}/outline-requests`)
      .flush(problem('ai.quota.forbidden'), { status: 403, statusText: 'Forbidden' });

    expect(['forbidden', 'account_suspended', 'quota_exhausted']).toContain((await pending).status);
  });

  it('watches one outline request and hands back its operation', async () => {
    const pending = firstValueFrom(service.watchOutline('cozy-fall', 'd-1', 'r-1'));

    const request = http.expectOne(`${DRAFT}/outline-requests/r-1`);
    expect(request.request.method).toBe('GET');
    request.flush({ request: operation('Running'), draft: wireDraft() });

    const outcome = await pending;
    expect(outcome.status === 'found' && outcome.operation.status).toBe('Running');
  });

  it('answers a watch for another workspace’s request as not found', async () => {
    const pending = firstValueFrom(service.watchOutline('cozy-fall', 'd-1', 'r-9'));
    http.expectOne(`${DRAFT}/outline-requests/r-9`).flush(problem('x'), { status: 404, statusText: 'Not Found' });

    expect((await pending).status).toBe('not_found');
  });

  // ---- reading -----------------------------------------------------------------------------------------

  it('reads a draft with its plan and the server’s check of it', async () => {
    const pending = service.read('cozy-fall', 'd-1');

    http.expectOne(DRAFT).flush(
      wireDraft({
        planCheck: {
          isValid: false,
          policyVersion: '1.0.0',
          findings: [{ code: 'content.long_form.heading.blank', sectionId: 's-1' }],
        },
      }),
    );

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    if (outcome.status !== 'found') return;
    expect(outcome.draft.latest?.sections[0]?.heading).toBe('Why this cake');
    expect(outcome.draft.planCheck?.findings).toEqual([
      { code: 'content.long_form.heading.blank', sectionId: 's-1' },
    ]);
  });

  it('gives one answer for a draft that is missing and one that is another workspace’s', async () => {
    const pending = service.read('cozy-fall', 'd-other');
    http.expectOne(`${DRAFTS}/d-other`).flush(problem('x'), { status: 404, statusText: 'Not Found' });

    expect(await pending).toEqual({ status: 'not_found' });
  });

  it('is unavailable rather than partly shown when one section cannot be read', async () => {
    const pending = service.read('cozy-fall', 'd-1');
    http.expectOne(DRAFT).flush(
      wireDraft({
        latest: {
          revisionNumber: 1,
          source: 'AiGenerated',
          createdAt: '2026-10-12T12:00:00Z',
          sections: [wireSection(), wireSection({ id: 's-2', role: 'Sidebar' })],
        },
      }),
    );

    expect(await pending).toEqual({ status: 'unavailable' });
  });

  it('lists with only the filters that were given', async () => {
    const pending = service.list('cozy-fall', { limit: 10, kind: 'BlogArticle', cursor: 'abc' });

    const request = http.expectOne((candidate) => candidate.url === DRAFTS);
    expect(request.request.params.get('limit')).toBe('10');
    expect(request.request.params.get('kind')).toBe('BlogArticle');
    expect(request.request.params.get('cursor')).toBe('abc');
    expect(request.request.params.has('creativeContextId')).toBeFalse();
    expect(request.request.params.keys().some((key) => key.toLowerCase().includes('workspace'))).toBeFalse();

    request.flush({
      items: [
        {
          id: 'd-1',
          creativeContextId: 'ctx-1',
          kind: 'BlogArticle',
          status: 'Outlined',
          latestRevisionNumber: 2,
          updatedAt: '2026-10-12T12:00:00Z',
        },
      ],
      nextCursor: null,
    });

    const outcome = await pending;
    expect(outcome.status === 'found' && outcome.page.items.length).toBe(1);
  });

  it('says a cursor that was not issued for this list is invalid', async () => {
    const pending = service.list('cozy-fall', { cursor: 'someone-elses' });
    http
      .expectOne((candidate) => candidate.url === DRAFTS)
      .flush(problem('content.long_form.cursor.invalid'), { status: 400, statusText: 'Bad Request' });

    expect(await pending).toEqual({ status: 'cursor_invalid' });
  });

  // ---- writing the plan --------------------------------------------------------------------------------

  it('patches the whole plan in order, with ids only on sections the plan already has', async () => {
    const pending = service.patchPlan('cozy-fall', 'd-1', {
      expectedConcurrencyToken: 'AAAAAAAAB9E=',
      sections: [
        { id: 's-2', role: 'Tips', heading: 'Three tips', intent: 'What goes wrong.', sourceReferenceId: null },
        { id: null, role: 'Body', heading: 'On olive oil', intent: 'Which to buy.', sourceReferenceId: null },
        { id: 's-3', role: 'Item', heading: 'The cake', intent: 'The recipe.', sourceReferenceId: 'ref-1' },
      ],
    });

    const request = http.expectOne(`${DRAFT}/plan`);
    expect(request.request.method).toBe('PATCH');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.body).toEqual({
      expectedConcurrencyToken: 'AAAAAAAAB9E=',
      sections: [
        { id: 's-2', role: 'Tips', heading: 'Three tips', intent: 'What goes wrong.' },
        { role: 'Body', heading: 'On olive oil', intent: 'Which to buy.' },
        { id: 's-3', role: 'Item', heading: 'The cake', intent: 'The recipe.', sourceReferenceId: 'ref-1' },
      ],
    });
    request.flush(wireDraft({ concurrencyToken: 'AAAAAAAAB9I=' }));

    const outcome = await pending;
    expect(outcome.status === 'saved' && outcome.draft.concurrencyToken).toBe('AAAAAAAAB9I=');
  });

  it('approves one revision with the token it was read with', async () => {
    const pending = service.approvePlan('cozy-fall', 'd-1', {
      expectedConcurrencyToken: 'AAAAAAAAB9E=',
      revisionNumber: 3,
    });

    const request = http.expectOne(`${DRAFT}/plan/approval`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=', revisionNumber: 3 });
    request.flush(wireDraft({ status: 'Outlined' }));

    const outcome = await pending;
    expect(outcome.status === 'saved' && outcome.draft.status).toBe('Outlined');
  });

  it('tells the three conflicts apart by their codes, not by the 409 they share', async () => {
    const cases: readonly (readonly [string, string])[] = [
      ['content.long_form.draft.stale.conflict', 'stale'],
      ['content.long_form.plan.locked.conflict', 'locked'],
      ['content.long_form.plan.not_started.conflict', 'not_started'],
    ];

    for (const [code, expected] of cases) {
      const pending = service.patchPlan('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', sections: [] });
      http.expectOne(`${DRAFT}/plan`).flush(problem(code), { status: 409, statusText: 'Conflict' });

      expect((await pending).status).toBe(expected);
    }
  });

  it('carries a refused approval’s section errors exactly as the server keyed them', async () => {
    const pending = service.approvePlan('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', revisionNumber: 1 });

    http.expectOne(`${DRAFT}/plan/approval`).flush(
      problem('content.long_form.plan.unprocessable', {
        errors: { 'sections[1]': ['Give this section a heading.'], sections: ['A plan needs an opening.'] },
      }),
      { status: 422, statusText: 'Unprocessable' },
    );

    expect(await pending).toEqual({
      status: 'refused',
      message: 'The server’s own sentence.',
      fieldErrors: { 'sections[1]': ['Give this section a heading.'], sections: ['A plan needs an opening.'] },
    });
  });

  it('reports a plan the server could not keep as refused, and a missing draft as not found', async () => {
    const invalid = service.patchPlan('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', sections: [] });
    http
      .expectOne(`${DRAFT}/plan`)
      .flush(problem('content.long_form.plan.invalid', { errors: { 'sections[0].id': ['Unknown section.'] } }), {
        status: 400,
        statusText: 'Bad Request',
      });
    expect((await invalid).status).toBe('refused');

    const missing = service.patchPlan('cozy-fall', 'd-9', { expectedConcurrencyToken: 't', sections: [] });
    http.expectOne(`${DRAFTS}/d-9/plan`).flush(problem('x'), { status: 404, statusText: 'Not Found' });
    expect(await missing).toEqual({ status: 'not_found' });

    const viewer = service.patchPlan('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', sections: [] });
    http.expectOne(`${DRAFT}/plan`).flush(problem('x'), { status: 403, statusText: 'Forbidden' });
    expect(await viewer).toEqual({ status: 'forbidden' });

    const down = service.patchPlan('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', sections: [] });
    http.expectOne(`${DRAFT}/plan`).error(new ProgressEvent('error'));
    expect(await down).toEqual({ status: 'unavailable' });
  });
});
