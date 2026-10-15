import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { ContentBoardService } from './content-board.service';

const GATEWAY = 'https://gateway.example';
const CTX = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11';
const URL = `${GATEWAY}/api/v1/workspaces/cozy-fall/creative-contexts/${CTX}/board-card`;

function detail(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    card: { id: 'card-1', title: 'Soda bread, autumn', column: 'ToDo', concurrencyToken: 'AAA=', ...overrides },
    description: null,
    lineage: [],
    history: [],
    historyTruncated: false,
    package: null,
  };
}

function problem(code: string): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1' };
}

describe('ContentBoardService', () => {
  let service: ContentBoardService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(ContentBoardService);
  });

  afterEach(() => http.verify());

  describe('pipelineCard', () => {
    it('reads the work’s card from its own route, with credentials', async () => {
      const pending = firstValueFrom(service.pipelineCard('cozy-fall', CTX));

      const request = http.expectOne(URL);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBe(true);
      request.flush(detail({ column: 'InProcess' }));

      expect(await pending).toEqual({
        status: 'found',
        card: { id: 'card-1', title: 'Soda bread, autumn', column: 'InProcess' },
      });
    });

    it('reads a 404 as work that is not on the board, which is not a failure', async () => {
      const pending = firstValueFrom(service.pipelineCard('cozy-fall', CTX));
      http.expectOne(URL).flush(problem('content.card.not_found'), { status: 404, statusText: 'Not Found' });

      expect(await pending).toEqual({ status: 'none' });
    });

    it('is unavailable for any other failure, and for a body that is not a card', async () => {
      const failed = firstValueFrom(service.pipelineCard('cozy-fall', CTX));
      http.expectOne(URL).flush(null, { status: 503, statusText: 'Service Unavailable' });
      expect(await failed).toEqual({ status: 'unavailable' });

      const unreadable = firstValueFrom(service.pipelineCard('cozy-fall', CTX));
      http.expectOne(URL).flush(detail({ column: 'Published' }));
      expect(await unreadable).toEqual({ status: 'unavailable' });
    });
  });

  describe('commitPipeline', () => {
    it('posts no body and no key: the server makes the card from the work', async () => {
      const pending = firstValueFrom(service.commitPipeline('cozy-fall', CTX));

      const request = http.expectOne(URL);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toBeNull();
      expect(request.request.withCredentials).toBe(true);
      expect(request.request.headers.has('Idempotency-Key')).toBe(false);
      request.flush(detail(), { status: 201, statusText: 'Created' });

      expect(await pending).toEqual({
        status: 'on_board',
        card: { id: 'card-1', title: 'Soda bread, autumn', column: 'ToDo' },
        created: true,
      });
    });

    it('reads a 200 as the card that was already there', async () => {
      const pending = firstValueFrom(service.commitPipeline('cozy-fall', CTX));
      http.expectOne(URL).flush(detail(), { status: 200, statusText: 'OK' });

      const outcome = await pending;
      expect(outcome.status === 'on_board' && !outcome.created).toBe(true);
    });

    const refusals: readonly (readonly [number, string, string])[] = [
      [422, 'content.card.pipeline.unprocessable', 'no_saved_picture'],
      [404, 'content.creative_context.not_found', 'not_found'],
      [403, 'content.card.forbidden', 'forbidden'],
      [409, 'content.card.conflict', 'unavailable'],
      [500, 'server.error', 'unavailable'],
    ];

    for (const [status, code, expected] of refusals) {
      it(`reads ${status} ${code} as ${expected}`, async () => {
        const pending = firstValueFrom(service.commitPipeline('cozy-fall', CTX));
        http.expectOne(URL).flush(problem(code), { status, statusText: 'Refused' });

        expect((await pending).status).toBe(expected);
      });
    }
  });

  // ---- The board's own routes (13.8) ----

  const CARDS = `${GATEWAY}/api/v1/workspaces/cozy-fall/content-cards`;
  // A real token shape: base64 carries `+`, `/` and `=`.
  const TOKEN = 'AAA+AAA/B9E=';

  function card(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
      id: 'card-1',
      title: 'Soda bread, autumn',
      descriptionPreview: 'Hero shot and the three posts.',
      descriptionLength: 30,
      column: 'ToDo',
      channelKey: 'instagram',
      plannedDate: '2026-10-16',
      plannedDay: 'Friday',
      cuisineCode: null,
      dishTypeCode: null,
      methodCode: null,
      lineage: {
        creativeContextId: CTX,
        recipeId: null,
        recipeVersionId: null,
        promptRecordId: null,
        mediaAssetId: 'asset-1',
        mediaAssetVersionNumber: 1,
        socialPackageId: null,
      },
      moveCount: 2,
      hasPackage: false,
      publishing: null,
      createdAt: '2026-10-13T09:00:00+00:00',
      updatedAt: '2026-10-13T10:00:00+00:00',
      concurrencyToken: TOKEN,
      ...overrides,
    };
  }

  function full(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
      card: card(),
      description: 'Hero shot and the three posts.',
      lineage: [{ kind: 'MediaAsset', id: 'asset-1', versionId: null, versionNumber: 1, label: null, available: true }],
      history: [
        { sequence: 1, fromColumn: null, toColumn: 'ToDo', note: null, occurredAt: '2026-10-13T09:00:00+00:00' },
        { sequence: 2, fromColumn: 'ToDo', toColumn: 'InProcess', note: 'Shoot booked', occurredAt: '2026-10-13T10:00:00+00:00' },
      ],
      historyTruncated: false,
      package: null,
      ...overrides,
    };
  }

  const FIELDS = { title: 'Soda bread', description: 'Notes', channelKey: 'instagram', plannedDate: '2026-10-16' };

  describe('list', () => {
    it('asks for one column with the filters, the cursor and the page size', async () => {
      const pending = firstValueFrom(
        service.list('cozy-fall', { channel: 'instagram', day: 'Friday', date: '2026-10-16' }, 'ToDo', 'cur+/=', 25),
      );

      const request = http.expectOne((each) => each.url === CARDS);
      expect(request.request.withCredentials).toBe(true);
      expect(request.request.params.get('channel')).toBe('instagram');
      expect(request.request.params.get('day')).toBe('Friday');
      expect(request.request.params.get('date')).toBe('2026-10-16');
      expect(request.request.params.get('column')).toBe('ToDo');
      expect(request.request.params.get('cursor')).toBe('cur+/=');
      expect(request.request.params.get('limit')).toBe('25');
      request.flush({ items: [card()], nextCursor: 'next', columns: [{ column: 'ToDo', count: 7 }] });

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status !== 'found') return;
      expect(outcome.page.items[0].title).toBe('Soda bread, autumn');
      expect(outcome.page.items[0].plannedDay).toBe('Friday');
      expect(outcome.page.items[0].publishing).toBeNull();
      expect(outcome.page.nextCursor).toBe('next');
      expect(outcome.page.columns).toEqual([{ column: 'ToDo', count: 7 }]);
    });

    it('sends no parameter for a filter that is not set', async () => {
      const pending = firstValueFrom(service.list('cozy-fall', { channel: null, day: null, date: null }));

      const request = http.expectOne((each) => each.url === CARDS);
      expect(request.request.params.keys()).toEqual([]);
      request.flush({ items: [], nextCursor: null, columns: [] });
      await pending;
    });

    it('fails the whole page on one card it cannot read, rather than dropping it', async () => {
      const pending = firstValueFrom(service.list('cozy-fall', { channel: null, day: null, date: null }));
      http
        .expectOne((each) => each.url === CARDS)
        .flush({ items: [card(), card({ id: 'card-2', column: 'Published' })], nextCursor: null, columns: [] });

      expect(await pending).toEqual({ status: 'unavailable' });
    });

    it('reads a refused cursor as one to start again from, and anything else as unavailable', async () => {
      const expired = firstValueFrom(service.list('cozy-fall', { channel: null, day: null, date: null }, 'ToDo', 'old'));
      http
        .expectOne((each) => each.url === CARDS)
        .flush(problem('content.card.cursor.invalid'), { status: 400, statusText: 'Bad Request' });
      expect(await expired).toEqual({ status: 'cursor_expired' });

      const failed = firstValueFrom(service.list('cozy-fall', { channel: null, day: null, date: null }));
      http.expectOne((each) => each.url === CARDS).flush(null, { status: 500, statusText: 'Server Error' });
      expect(await failed).toEqual({ status: 'unavailable' });
    });
  });

  describe('detail', () => {
    it('reads a card in full, with what it points at and where it has been', async () => {
      const pending = firstValueFrom(service.detail('cozy-fall', 'card-1'));
      http.expectOne(`${CARDS}/card-1`).flush(full());

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status !== 'found') return;
      expect(outcome.detail.description).toBe('Hero shot and the three posts.');
      expect(outcome.detail.lineage[0]).toEqual({
        kind: 'MediaAsset',
        id: 'asset-1',
        versionId: null,
        versionNumber: 1,
        label: null,
        available: true,
      });
      expect(outcome.detail.history.map((move) => move.fromColumn)).toEqual([null, 'ToDo']);
      expect(outcome.detail.history[1].note).toBe('Shoot booked');
    });

    it('gives one answer for a card that is unknown, deleted or another workspace’s', async () => {
      const pending = firstValueFrom(service.detail('cozy-fall', 'card-1'));
      http.expectOne(`${CARDS}/card-1`).flush(problem('content.card.not_found'), { status: 404, statusText: 'Not Found' });

      expect(await pending).toEqual({ status: 'not_found' });
    });
  });

  describe('create', () => {
    it('posts only what was filled in, to the column chosen, under the caller’s key', async () => {
      const pending = firstValueFrom(
        service.create('cozy-fall', { title: 'Soda bread', description: null, channelKey: null, plannedDate: null }, 'InProcess', 'key-1'),
      );

      const request = http.expectOne(CARDS);
      expect(request.request.method).toBe('POST');
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body).toEqual({ title: 'Soda bread', column: 'InProcess' });
      request.flush(full(), { status: 201, statusText: 'Created' });

      expect((await pending).status).toBe('saved');
    });

    it('names the records a card links when it is made for some, and leaves out what it does not link (13A.20)', async () => {
      const pending = firstValueFrom(
        service.create('cozy-fall', { title: 'Soda bread', description: null, channelKey: null, plannedDate: null }, 'ToDo', 'key-2', {
          creativeContextId: 'ctx-1',
          recipeId: 'recipe-1',
          recipeVersionId: null,
          mediaAssetId: 'asset-1',
          mediaAssetVersionNumber: 2,
          socialPackageId: 'pkg-1',
        }),
      );

      const request = http.expectOne(CARDS);
      expect(request.request.body).toEqual({
        title: 'Soda bread',
        column: 'ToDo',
        creativeContextId: 'ctx-1',
        recipeId: 'recipe-1',
        mediaAssetId: 'asset-1',
        mediaAssetVersionNumber: 2,
        socialPackageId: 'pkg-1',
      });
      request.flush(full(), { status: 201, statusText: 'Created' });

      expect((await pending).status).toBe('saved');
    });

    it('carries the server’s field errors for a body it refuses', async () => {
      const pending = firstValueFrom(service.create('cozy-fall', FIELDS, 'ToDo', 'key-1'));
      http
        .expectOne(CARDS)
        .flush({ ...problem('content.card.invalid'), errors: { title: ['A card needs a title.'] } }, { status: 400, statusText: 'Bad Request' });

      expect(await pending).toEqual({ status: 'invalid', errors: { title: ['A card needs a title.'] } });
    });

    it('tells a reused key from a refusal', async () => {
      const pending = firstValueFrom(service.create('cozy-fall', FIELDS, 'ToDo', 'key-1'));
      http.expectOne(CARDS).flush(problem('idempotency.key_reused'), { status: 422, statusText: 'Unprocessable' });

      expect(await pending).toEqual({ status: 'key_reused' });
    });
  });

  describe('update', () => {
    it('sends only the fields that changed, and null for one that was emptied', async () => {
      const pending = firstValueFrom(
        service.update('cozy-fall', 'card-1', FIELDS, { ...FIELDS, title: 'Soda bread, Friday', description: null }, TOKEN),
      );

      const request = http.expectOne(`${CARDS}/card-1`);
      expect(request.request.method).toBe('PATCH');
      expect(request.request.body).toEqual({ expectedConcurrencyToken: TOKEN, title: 'Soda bread, Friday', description: null });
      request.flush(full());

      expect((await pending).status).toBe('saved');
    });

    it('reads a stale card as stale only when the server says so by name', async () => {
      const stale = firstValueFrom(service.update('cozy-fall', 'card-1', FIELDS, { ...FIELDS, title: 'x' }, TOKEN));
      http.expectOne(`${CARDS}/card-1`).flush(problem('content.card.stale.conflict'), { status: 409, statusText: 'Conflict' });
      expect(await stale).toEqual({ status: 'stale' });

      const other = firstValueFrom(service.update('cozy-fall', 'card-1', FIELDS, { ...FIELDS, title: 'x' }, TOKEN));
      http.expectOne(`${CARDS}/card-1`).flush(problem('content.card.conflict'), { status: 409, statusText: 'Conflict' });
      expect(await other).toEqual({ status: 'unavailable' });
    });
  });

  describe('move', () => {
    it('names the column and the card to go before, with the token and the note', async () => {
      const pending = firstValueFrom(
        service.move('cozy-fall', 'card-1', { column: 'InProcess', beforeCardId: 'card-2', note: 'Shoot booked' }, TOKEN),
      );

      const request = http.expectOne(`${CARDS}/card-1/move`);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        expectedConcurrencyToken: TOKEN,
        column: 'InProcess',
        beforeCardId: 'card-2',
        note: 'Shoot booked',
      });
      request.flush(full());

      expect((await pending).status).toBe('saved');
    });

    it('leaves out the neighbour for the bottom of a column, and the note when there is none', async () => {
      const pending = firstValueFrom(service.move('cozy-fall', 'card-1', { column: 'Approved', beforeCardId: null }, TOKEN));

      const request = http.expectOne(`${CARDS}/card-1/move`);
      expect(request.request.body).toEqual({ expectedConcurrencyToken: TOKEN, column: 'Approved' });
      request.flush(full());
      await pending;
    });

    const refusals: readonly (readonly [number, string, string])[] = [
      [409, 'content.card.stale.conflict', 'stale'],
      [422, 'content.card.move.unprocessable', 'refused'],
      [404, 'content.card.not_found', 'not_found'],
      [403, 'content.card.forbidden', 'forbidden'],
      [400, 'content.card.invalid', 'invalid'],
      [503, 'server.unavailable', 'unavailable'],
    ];

    for (const [status, code, expected] of refusals) {
      it(`reads ${status} ${code} as ${expected}`, async () => {
        const pending = firstValueFrom(service.move('cozy-fall', 'card-1', { column: 'Approved', beforeCardId: null }, TOKEN));
        http.expectOne(`${CARDS}/card-1/move`).flush(problem(code), { status, statusText: 'Refused' });

        expect((await pending).status).toBe(expected);
      });
    }
  });

  describe('remove', () => {
    it('confirms, quotes the token, carries the key, and never asks for the picture to be removed', async () => {
      const pending = firstValueFrom(service.remove('cozy-fall', 'card-1', TOKEN, 'key-9'));

      const request = http.expectOne(`${CARDS}/card-1`);
      expect(request.request.method).toBe('DELETE');
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-9');
      expect(request.request.body).toEqual({ confirmed: true, expectedConcurrencyToken: TOKEN });
      request.flush({ id: 'card-1' });

      expect(await pending).toEqual({ status: 'deleted' });
    });

    const refusals: readonly (readonly [number, string, string])[] = [
      [404, 'content.card.not_found', 'not_found'],
      [403, 'content.card.forbidden', 'forbidden'],
      [409, 'content.card.stale.conflict', 'stale'],
      [409, 'content.card.linked_asset.conflict', 'unavailable'],
      [500, 'server.error', 'unavailable'],
    ];

    for (const [status, code, expected] of refusals) {
      it(`reads ${status} ${code} as ${expected}`, async () => {
        const pending = firstValueFrom(service.remove('cozy-fall', 'card-1', TOKEN, 'key-9'));
        http.expectOne(`${CARDS}/card-1`).flush(problem(code), { status, statusText: 'Refused' });

        expect((await pending).status).toBe(expected);
      });
    }
  });
});
