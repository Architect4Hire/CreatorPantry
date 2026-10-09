import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { CreativeContextService } from './creative-context.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/creative-contexts`;
const ID = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11';
const REFERENCE = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a12';
const IMAGE = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a13';

// A real token shape: base64 carries `+`, `/` and `=`, which is exactly what a query string mangles.
const TOKEN = 'AAA+AAA/B9E=';

function contextBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: ID,
    workingTitle: 'Soda bread, autumn',
    pictureBrief: null,
    channelKeys: ['blog'],
    day: null,
    weeklyThemeKey: null,
    references: [],
    createdAt: '2026-10-09T12:00:00+00:00',
    updatedAt: '2026-10-09T12:00:00+00:00',
    archivedAt: null,
    concurrencyToken: TOKEN,
    ...overrides,
  };
}

function problem(code: string, errors?: Record<string, string[]>): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1', ...(errors ? { errors } : {}) };
}

describe('CreativeContextService', () => {
  let service: CreativeContextService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(CreativeContextService);
  });

  afterEach(() => http.verify());

  describe('create', () => {
    it('posts the draft to the workspace route with credentials and the idempotency key', async () => {
      const pending = firstValueFrom(
        service.create(
          'cozy-fall',
          { workingTitle: 'Soda bread, autumn', from: { kind: 'GeneratedImage', generatedImageId: IMAGE } },
          'key-1',
        ),
      );

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('POST');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body).toEqual({
        workingTitle: 'Soda bread, autumn',
        from: { kind: 'GeneratedImage', generatedImageId: IMAGE },
      });
      request.flush(contextBody(), { status: 201, statusText: 'Created' });

      const outcome = await pending;
      expect(outcome.status).toBe('created');
      if (outcome.status === 'created') expect(outcome.context.id).toBe(ID);
    });

    it('names the workspace in the path only, encoded', () => {
      service.create('a slug/with?odd', {}, 'key-1').subscribe();

      const request = http.expectOne(`${GATEWAY}/api/v1/workspaces/a%20slug%2Fwith%3Fodd/creative-contexts`);
      expect(JSON.stringify(request.request.body)).not.toContain('workspace');
      request.flush(contextBody(), { status: 201, statusText: 'Created' });
    });

    const refusals = [
      [400, 'content.creative_context.invalid', 'invalid'],
      [403, 'content.creative_context.forbidden', 'forbidden'],
      [422, 'content.creative_context.reference.unprocessable', 'source_unavailable'],
      [422, 'content.creative_context.channel.unprocessable', 'source_unavailable'],
      [422, 'content.creative_context.theme.unprocessable', 'source_unavailable'],
      [422, 'idempotency.key_reused', 'key_reused'],
      [409, 'content.creative_context.conflict', 'unavailable'],
      [500, 'internal', 'unavailable'],
      [0, 'network', 'unavailable'],
    ] as const;

    for (const [status, code, expected] of refusals) {
      it(`answers '${expected}' to a ${status} ${code}`, async () => {
        const pending = firstValueFrom(service.create('cozy-fall', {}, 'key-1'));

        const request = http.expectOne(BASE);
        if (status === 0) request.error(new ProgressEvent('error'));
        else request.flush(problem(code), { status, statusText: 'Refused' });

        expect((await pending).status).toBe(expected);
      });
    }

    it('carries the field errors of a 400', async () => {
      const pending = firstValueFrom(service.create('cozy-fall', {}, 'key-1'));

      http
        .expectOne(BASE)
        .flush(problem('content.creative_context.invalid', { workingTitle: ['Too long.'] }), {
          status: 400,
          statusText: 'Bad Request',
        });

      const outcome = await pending;
      expect(outcome.status).toBe('invalid');
      if (outcome.status === 'invalid') expect(outcome.errors['workingTitle']).toEqual(['Too long.']);
    });

    it('treats a body it cannot read as unavailable, never as a half-built context', async () => {
      const pending = firstValueFrom(service.create('cozy-fall', {}, 'key-1'));

      http.expectOne(BASE).flush({ id: ID }, { status: 201, statusText: 'Created' });

      expect((await pending).status).toBe('unavailable');
    });
  });

  describe('get', () => {
    it('reads one context', async () => {
      const pending = firstValueFrom(service.get('cozy-fall', ID));

      const request = http.expectOne(`${BASE}/${ID}`);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      request.flush(contextBody());

      expect((await pending).status).toBe('found');
    });

    it("answers 'not_found' to a 404, whoever the context belongs to", async () => {
      const pending = firstValueFrom(service.get('cozy-fall', ID));

      http.expectOne(`${BASE}/${ID}`).flush(problem('content.creative_context.not_found'), {
        status: 404,
        statusText: 'Not Found',
      });

      expect((await pending).status).toBe('not_found');
    });

    it("answers 'unavailable' to anything else", async () => {
      const pending = firstValueFrom(service.get('cozy-fall', ID));

      http.expectOne(`${BASE}/${ID}`).flush(problem('internal'), { status: 503, statusText: 'Unavailable' });

      expect((await pending).status).toBe('unavailable');
    });
  });

  describe('list', () => {
    const page = {
      items: [
        {
          id: ID,
          workingTitle: 'Soda bread, autumn',
          channelKeys: ['blog'],
          day: null,
          weeklyThemeKey: null,
          referenceCount: 1,
          updatedAt: '2026-10-09T12:00:00+00:00',
        },
      ],
      nextCursor: 'next',
    };

    it('asks for the first page with no parameters', async () => {
      const pending = firstValueFrom(service.list('cozy-fall'));

      const request = http.expectOne(BASE);
      expect(request.request.params.keys()).toEqual([]);
      request.flush(page);

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') expect(outcome.page.nextCursor).toBe('next');
    });

    it('passes the cursor and limit through as given', () => {
      service.list('cozy-fall', 'abc+/=', 10).subscribe();

      const request = http.expectOne((candidate) => candidate.url === BASE);
      expect(request.request.params.get('cursor')).toBe('abc+/=');
      expect(request.request.params.get('limit')).toBe('10');
      request.flush(page);
    });

    it("answers 'cursor_expired' when the server says the cursor is not this list's", async () => {
      const pending = firstValueFrom(service.list('cozy-fall', 'stale'));

      http
        .expectOne((candidate) => candidate.url === BASE)
        .flush(problem('content.creative_context.cursor.invalid'), { status: 400, statusText: 'Bad Request' });

      expect((await pending).status).toBe('cursor_expired');
    });

    it("answers 'unavailable' to a 400 that is about something else", async () => {
      const pending = firstValueFrom(service.list('cozy-fall', 'stale'));

      http
        .expectOne((candidate) => candidate.url === BASE)
        .flush(problem('bad_request'), { status: 400, statusText: 'Bad Request' });

      expect((await pending).status).toBe('unavailable');
    });
  });

  describe('patch', () => {
    it('sends the token and only the fields that were set, null included', async () => {
      const pending = firstValueFrom(service.patch('cozy-fall', ID, { workingTitle: 'Renamed', day: null }, TOKEN));

      const request = http.expectOne(`${BASE}/${ID}`);
      expect(request.request.method).toBe('PATCH');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.body).toEqual({ expectedConcurrencyToken: TOKEN, workingTitle: 'Renamed', day: null });
      request.flush(contextBody({ workingTitle: 'Renamed', concurrencyToken: 'next' }));

      const outcome = await pending;
      expect(outcome.status).toBe('saved');
      if (outcome.status === 'saved') expect(outcome.context.concurrencyToken).toBe('next');
    });
  });

  describe('addReference', () => {
    it('posts the source and the token to the references route', async () => {
      const pending = firstValueFrom(
        service.addReference('cozy-fall', ID, { kind: 'GeneratedImage', generatedImageId: IMAGE }, TOKEN),
      );

      const request = http.expectOne(`${BASE}/${ID}/references`);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        expectedConcurrencyToken: TOKEN,
        reference: { kind: 'GeneratedImage', generatedImageId: IMAGE },
      });
      request.flush(contextBody(), { status: 201, statusText: 'Created' });

      expect((await pending).status).toBe('saved');
    });
  });

  describe('removeReference', () => {
    it('deletes the reference by its own id, with the token as an encoded query parameter', async () => {
      const pending = firstValueFrom(service.removeReference('cozy-fall', ID, REFERENCE, TOKEN));

      const request = http.expectOne((candidate) => candidate.url === `${BASE}/${ID}/references/${REFERENCE}`);
      expect(request.request.method).toBe('DELETE');
      expect(request.request.body).toBeNull();
      expect(request.request.params.get('expectedConcurrencyToken')).toBe(TOKEN);

      // What actually goes on the wire. The `+` is the one that matters: left bare it would arrive as a space
      // and never match a token. `/` and `=` are legal in a query string and travel as they are.
      expect(request.request.urlWithParams).toContain('expectedConcurrencyToken=AAA%2BAAA/B9E=');
      request.flush(contextBody());

      expect((await pending).status).toBe('saved');
    });
  });

  describe('every write', () => {
    const writes = [
      ['patch', () => service.patch('cozy-fall', ID, { workingTitle: 'Renamed' }, TOKEN), `${BASE}/${ID}`],
      [
        'addReference',
        () => service.addReference('cozy-fall', ID, { kind: 'GeneratedImage', generatedImageId: IMAGE }, TOKEN),
        `${BASE}/${ID}/references`,
      ],
      [
        'removeReference',
        () => service.removeReference('cozy-fall', ID, REFERENCE, TOKEN),
        `${BASE}/${ID}/references/${REFERENCE}`,
      ],
    ] as const;

    const refusals = [
      [400, 'content.creative_context.invalid', 'invalid'],
      [403, 'content.creative_context.forbidden', 'forbidden'],
      [404, 'content.creative_context.not_found', 'not_found'],
      [409, 'content.creative_context.stale.conflict', 'stale'],
      [409, 'content.creative_context.reference.duplicate.conflict', 'duplicate'],
      // A conflict it has no name for is not a reason to tell a creator their work changed under them.
      [409, 'something.else.conflict', 'unavailable'],
      [422, 'content.creative_context.reference_limit.unprocessable', 'limit'],
      [422, 'content.creative_context.reference.unprocessable', 'source_unavailable'],
      [422, 'content.creative_context.channel.unprocessable', 'source_unavailable'],
      [500, 'internal', 'unavailable'],
    ] as const;

    for (const [name, call, url] of writes) {
      for (const [status, code, expected] of refusals) {
        it(`${name} answers '${expected}' to a ${status} ${code}`, async () => {
          const pending = firstValueFrom(call());

          http
            .expectOne((candidate) => candidate.url === url)
            .flush(problem(code), { status, statusText: 'Refused' });

          expect((await pending).status).toBe(expected);
        });
      }

      it(`${name} treats a body it cannot read as unavailable`, async () => {
        const pending = firstValueFrom(call());

        http.expectOne((candidate) => candidate.url === url).flush({ nonsense: true });

        expect((await pending).status).toBe('unavailable');
      });
    }
  });
});
