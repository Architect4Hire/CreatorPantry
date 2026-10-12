import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import {
  decodeLongFormAssembledDocument,
  decodeLongFormDocumentReview,
  encodeRequestLongFormSection,
  longFormDocumentIsOpen,
  longFormEditorialPartLabel,
} from '../models/long-form-document.models';
import { LongFormDocumentService } from './long-form-document.service';

const DRAFT = 'https://gateway.example/api/v1/workspaces/cozy-fall/long-form-drafts/d-1';

function wireSection(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 's-1',
    role: 'Hook',
    heading: 'Why this cake',
    intent: 'Open on the smell of it.',
    body: 'Some cakes ask for an afternoon.',
    isWritten: true,
    settled: false,
    characterCount: 31,
    overCeiling: false,
    writtenBy: 'AiGenerated',
    aiProposalId: 'p-1',
    reusedEditorialParts: [],
    findings: [],
    ...overrides,
  };
}

function wireDocument(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    draftId: 'd-1',
    creativeContextId: 'ctx-1',
    kind: 'BlogArticle',
    status: 'Writing',
    revisionNumber: 4,
    concurrencyToken: 'AAAAAAAAB9E=',
    markdown: '## Why this cake\n\nSome cakes ask for an afternoon.\n',
    wordCount: 6,
    characterCount: 31,
    targetWords: { min: 900, max: 1600 },
    wordTarget: 'Under',
    sectionMaxCharacters: 3000,
    documentMaxCharacters: 20000,
    documentOverCeiling: false,
    sections: [wireSection()],
    needsConfirmation: [],
    acceptedRevisionNumber: null,
    acceptedAt: null,
    ...overrides,
  };
}

function operation(): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status: 'Requested',
    taskType: 'LongFormSection',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-12T12:00:00Z',
    statusChangedAt: '2026-10-12T12:00:00Z',
    failureCategory: null,
    proposal: null,
  };
}

function problem(code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { title: 'The server’s own sentence.', code, ...extra };
}

describe('LongFormDocumentService', () => {
  let service: LongFormDocumentService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(LongFormDocumentService);
  });

  afterEach(() => http.verify());

  // ---- reading -----------------------------------------------------------------------------------------

  it('reads the document with its warnings and requests', async () => {
    const pending = service.read('cozy-fall', 'd-1');

    const call = http.expectOne(`${DRAFT}/document`);
    expect(call.request.method).toBe('GET');
    expect(call.request.withCredentials).toBeTrue();
    call.flush({
      document: wireDocument({ needsConfirmation: [{ issue: 'OverCeiling', sectionIds: [] }] }),
      warnings: [{ sectionId: 's-1', kind: 'UnverifiedClaim', code: 'editorial.unsupported_number', message: 'A figure.', fromModel: false }],
      requests: [{ sectionId: 's-1', requestId: 'r-1', status: 'Proposed', failureCategory: null, outcome: 'Written', requestedAt: '2026-10-12T12:00:00Z' }],
    });

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    if (outcome.status !== 'found') return;
    expect(outcome.review.document.markdown).toContain('## Why this cake');
    expect(outcome.review.document.sections[0]!.findings).toEqual([]);
    expect(outcome.review.document.needsConfirmation).toEqual([{ issue: 'OverCeiling', sectionIds: [] }]);
    expect(outcome.review.warnings[0]!.code).toBe('editorial.unsupported_number');
    expect(outcome.review.requests[0]!.outcome).toBe('Written');
  });

  it('gives one answer for a draft that is missing and one that is another workspace’s', async () => {
    const pending = service.read('cozy-fall', 'd-1');
    http.expectOne(`${DRAFT}/document`).flush(problem('content.long_form.draft.not_found'), { status: 404, statusText: 'Not Found' });

    expect(await pending).toEqual({ status: 'not_found' });
  });

  it('is unavailable rather than partly shown when a section, an issue or a warning cannot be read', async () => {
    const broken: Record<string, unknown>[] = [
      { document: wireDocument({ sections: [wireSection(), wireSection({ id: 's-2', role: 'Sidebar' })] }), warnings: [], requests: [] },
      { document: wireDocument({ needsConfirmation: [{ issue: 'Typos', sectionIds: [] }] }), warnings: [], requests: [] },
      { document: wireDocument({ sections: [wireSection({ findings: [{ code: 'x', kind: 'Hunch', message: 'y' }] })] }), warnings: [], requests: [] },
      { document: wireDocument(), warnings: [{ sectionId: 's-1', kind: 'Hunch', code: null, message: 'x', fromModel: false }], requests: [] },
      { document: wireDocument(), warnings: [], requests: [{ sectionId: 's-1', requestId: 'r-1', status: 'Proposed', failureCategory: null, outcome: 'Lost', requestedAt: 'x' }] },
    ];

    for (const body of broken) {
      const pending = service.read('cozy-fall', 'd-1');
      http.expectOne(`${DRAFT}/document`).flush(body);

      expect(await pending).toEqual({ status: 'unavailable' });
    }
  });

  // ---- asking for writing ------------------------------------------------------------------------------

  it('asks for one section with the revision, the key, and an instruction only when there is one', async () => {
    const pending = service.requestSection('cozy-fall', 'd-1', 's-1', { revisionNumber: 4, instruction: '  Make it warmer.  ' }, 'key-1');

    const call = http.expectOne(`${DRAFT}/sections/s-1/writing-requests`);
    expect(call.request.method).toBe('POST');
    expect(call.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(call.request.body).toEqual({ revisionNumber: 4, instruction: 'Make it warmer.' });
    expect(JSON.stringify(call.request.body).toLowerCase()).not.toContain('workspace');
    call.flush({ draft: {}, request: { sectionId: 's-1', request: operation() } }, { status: 202, statusText: 'Accepted' });

    const outcome = await pending;
    expect(outcome.status === 'accepted' && outcome.requests[0]!.sectionId).toBe('s-1');

    expect(encodeRequestLongFormSection({ revisionNumber: 4, instruction: '   ' })).toEqual({ revisionNumber: 4 });
    expect(encodeRequestLongFormSection({ revisionNumber: 4 })).toEqual({ revisionNumber: 4 });
  });

  it('asks for the rest in one request and reads back one queued request to a section', async () => {
    const pending = service.requestRest('cozy-fall', 'd-1', 4, 'rest-1');

    const call = http.expectOne(`${DRAFT}/section-writing-batches`);
    expect(call.request.body).toEqual({ revisionNumber: 4 });
    expect(call.request.headers.get('Idempotency-Key')).toBe('rest-1');
    call.flush(
      { draft: {}, requests: [{ sectionId: 's-2', request: operation() }, { sectionId: 's-3', request: operation() }] },
      { status: 202, statusText: 'Accepted', headers: { 'Idempotent-Replayed': 'true' } },
    );

    const outcome = await pending;
    expect(outcome.status).toBe('accepted');
    if (outcome.status !== 'accepted') return;
    expect(outcome.requests.map((queued) => queued.sectionId)).toEqual(['s-2', 's-3']);
    expect(outcome.replayed).toBeTrue();
  });

  it('carries how many sections a short allowance could have covered', async () => {
    const pending = service.requestRest('cozy-fall', 'd-1', 4, 'rest-1');
    http.expectOne(`${DRAFT}/section-writing-batches`).flush(
      problem('ai.quota.exhausted', {
        unit: 'Credits',
        allowance: 100,
        remaining: 10,
        required: 60,
        resetsAt: '2026-11-01T00:00:00Z',
        sectionsRequested: 3,
        sectionsCoverable: 1,
      }),
      { status: 429, statusText: 'Too Many Requests' },
    );

    const outcome = await pending;
    expect(outcome.status).toBe('blocked');
    if (outcome.status !== 'blocked') return;
    expect(outcome.sectionsRequested).toBe(3);
    expect(outcome.sectionsCoverable).toBe(1);
  });

  it('tells a settled or changed section, a switched-off task, a reused key and a missing draft apart', async () => {
    const cases: readonly (readonly [number, string, string])[] = [
      [409, 'content.long_form.section.settled.conflict', 'refused'],
      [409, 'content.long_form.revision.stale.conflict', 'refused'],
      [400, 'ai.longFormSection.not_enabled', 'not_enabled'],
      [422, 'idempotency.key_reused', 'key_reused'],
      [404, 'content.long_form.draft.not_found', 'not_found'],
      [403, 'auth.forbidden', 'forbidden'],
      [500, 'server.error', 'unavailable'],
    ];

    for (const [status, code, expected] of cases) {
      const pending = service.requestSection('cozy-fall', 'd-1', 's-1', { revisionNumber: 4 }, 'key-1');
      http.expectOne(`${DRAFT}/sections/s-1/writing-requests`).flush(problem(code), { status, statusText: 'x' });

      expect((await pending).status).withContext(code).toBe(expected);
    }
  });

  // ---- the creator's own writes ------------------------------------------------------------------------

  it('saves a section’s text exactly as typed and a settled mark as it is', async () => {
    const edit = service.editSection('cozy-fall', 'd-1', 's-1', { expectedConcurrencyToken: 't', body: '  Mine.\n' });
    const patch = http.expectOne(`${DRAFT}/sections/s-1`);
    expect(patch.request.method).toBe('PATCH');
    expect(patch.request.body).toEqual({ expectedConcurrencyToken: 't', body: '  Mine.\n' });
    patch.flush({});
    expect(await edit).toEqual({ status: 'saved' });

    const settle = service.settleSection('cozy-fall', 'd-1', 's-1', { expectedConcurrencyToken: 't', settled: false });
    const post = http.expectOne(`${DRAFT}/sections/s-1/settlement`);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({ expectedConcurrencyToken: 't', settled: false });
    post.flush({});
    expect(await settle).toEqual({ status: 'saved' });
  });

  it('tells a moved draft from a refusal, and carries the refusal’s field errors', async () => {
    const stale = service.editSection('cozy-fall', 'd-1', 's-1', { expectedConcurrencyToken: 't', body: 'x' });
    http.expectOne(`${DRAFT}/sections/s-1`).flush(problem('content.long_form.draft.stale.conflict'), { status: 409, statusText: 'Conflict' });
    expect(await stale).toEqual({ status: 'stale' });

    const refused = service.editSection('cozy-fall', 'd-1', 's-1', { expectedConcurrencyToken: 't', body: 'x' });
    http
      .expectOne(`${DRAFT}/sections/s-1`)
      .flush(problem('content.long_form.section.invalid', { errors: { body: ['Too long.'] } }), { status: 400, statusText: 'Bad Request' });
    expect(await refused).toEqual({
      status: 'refused',
      code: 'content.long_form.section.invalid',
      message: 'The server’s own sentence.',
      fieldErrors: { body: ['Too long.'] },
    });

    const down = service.settleSection('cozy-fall', 'd-1', 's-1', { expectedConcurrencyToken: 't', settled: true });
    http.expectOne(`${DRAFT}/sections/s-1/settlement`).error(new ProgressEvent('error'));
    expect(await down).toEqual({ status: 'unavailable' });
  });

  // ---- accepting ---------------------------------------------------------------------------------------

  it('accepts with the token, the revision and exactly the issues confirmed', async () => {
    const pending = service.accept('cozy-fall', 'd-1', {
      expectedConcurrencyToken: 't',
      revisionNumber: 4,
      confirmed: ['UnwrittenSections'],
    });

    const call = http.expectOne(`${DRAFT}/acceptance`);
    expect(call.request.method).toBe('POST');
    expect(call.request.body).toEqual({ expectedConcurrencyToken: 't', revisionNumber: 4, confirmed: ['UnwrittenSections'] });
    call.flush(wireDocument({ status: 'Accepted', acceptedRevisionNumber: 4, acceptedAt: '2026-10-12T12:00:00Z' }));

    const outcome = await pending;
    expect(outcome.status === 'accepted' && outcome.document.status).toBe('Accepted');
  });

  it('reports what is wrong with the document when an acceptance did not confirm it', async () => {
    const pending = service.accept('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', revisionNumber: 4, confirmed: [] });
    http.expectOne(`${DRAFT}/acceptance`).flush(
      problem('content.long_form.acceptance.confirmation_required.unprocessable', {
        issues: [{ issue: 'UnwrittenSections', sectionIds: ['s-2'] }],
      }),
      { status: 422, statusText: 'Unprocessable' },
    );

    expect(await pending).toEqual({
      status: 'confirmation_required',
      issues: [{ issue: 'UnwrittenSections', sectionIds: ['s-2'] }],
    });
  });

  it('does not offer a confirmation it cannot name, and tells stale from refused', async () => {
    const unnamed = service.accept('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', revisionNumber: 4, confirmed: [] });
    http
      .expectOne(`${DRAFT}/acceptance`)
      .flush(problem('content.long_form.acceptance.confirmation_required.unprocessable', { issues: [{ issue: 'Mystery' }] }), { status: 422, statusText: 'x' });
    expect(await unnamed).toEqual({ status: 'unavailable' });

    const stale = service.accept('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', revisionNumber: 3, confirmed: [] });
    http.expectOne(`${DRAFT}/acceptance`).flush(problem('content.long_form.draft.stale.conflict'), { status: 409, statusText: 'x' });
    expect(await stale).toEqual({ status: 'stale' });

    const empty = service.accept('cozy-fall', 'd-1', { expectedConcurrencyToken: 't', revisionNumber: 4, confirmed: [] });
    http
      .expectOne(`${DRAFT}/acceptance`)
      .flush(problem('content.long_form.acceptance.nothing_written.unprocessable'), { status: 422, statusText: 'x' });
    expect((await empty).status).toBe('refused');
  });
});

describe('long-form document models', () => {
  it('reads a document with no plan, and refuses one whose shape is wrong', () => {
    expect(decodeLongFormAssembledDocument(wireDocument({ revisionNumber: null, sections: [], markdown: '' }))?.revisionNumber).toBeNull();
    expect(decodeLongFormAssembledDocument(wireDocument({ wordTarget: 'Roughly' }))).toBeNull();
    expect(decodeLongFormAssembledDocument(wireDocument({ targetWords: { min: '900' } }))).toBeNull();
    expect(decodeLongFormAssembledDocument(wireDocument({ concurrencyToken: '' }))).toBeNull();
    expect(decodeLongFormDocumentReview({ document: wireDocument() })).toBeNull();
  });

  it('keeps a document open until it is accepted or set aside', () => {
    const open = (status: string): boolean => longFormDocumentIsOpen(decodeLongFormAssembledDocument(wireDocument({ status }))!);

    expect(open('Outlined')).toBeTrue();
    expect(open('Writing')).toBeTrue();
    expect(open('Accepted')).toBeFalse();
    expect(open('NeedsReview')).toBeFalse();
  });

  it('names a part of accepted copy in plain words, and an unknown one as it is', () => {
    expect(longFormEditorialPartLabel('storageReheating')).toBe('storage and reheating notes');
    expect(longFormEditorialPartLabel('somethingNew')).toBe('somethingNew');
  });
});
