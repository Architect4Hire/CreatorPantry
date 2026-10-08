import {
  GENERATED_IMAGE_FAILURE_SENTENCES,
  GENERATED_IMAGE_PROMPT_MAX_LENGTH,
  GeneratedImageOperationStatus,
  avoidTextFor,
  decodeGeneratedImageOperation,
  decodeGeneratedImageOperationDetail,
  decodeStagedImage,
  encodeRequestGeneratedImages,
  generatedImageFailureSentence,
  isGeneratedImageOperationFailed,
  isGeneratedImageOperationFinished,
  isStagedImageActionable,
} from './generated-image.models';

/**
 * Every category `GeneratedImageFailureCategory` declares, spelled out rather than derived.
 *
 * A literal list on purpose: a category added server-side has to red a test here, because the alternative is a
 * creator being shown "no reason was recorded" for a failure the server explained perfectly well.
 */
const SERVER_FAILURE_CATEGORIES = [
  'provider-not-configured',
  'model-unidentified',
  'provider-unavailable',
  'rate-limited',
  'provider-refused',
  'invalid-response',
  'unsupported-media-type',
  'corrupt-image',
  'image-too-large',
  'malware-detected',
  'storage',
  'lease-abandoned',
];

/** The categories the server's own `Retryable` set names, which every "asking again" sentence must cover. */
const SERVER_RETRYABLE = ['provider-unavailable', 'rate-limited', 'storage'];

function stagedRow(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'img-1',
    variantIndex: 1,
    status: 'Staged',
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-09T12:00:00Z',
    createdAt: '2026-10-08T12:00:00Z',
    ...overrides,
  };
}

function operationBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'op-1',
    status: 'Requested',
    variantCount: 4,
    stagedCount: 0,
    providerName: null,
    modelName: null,
    failureCategory: null,
    failureSummary: null,
    requestedAt: '2026-10-08T12:00:00Z',
    completedAt: null,
    ...overrides,
  };
}

describe('generated-image.models', () => {
  describe('decodeStagedImage', () => {
    it('reads a whole row', () => {
      expect(decodeStagedImage(stagedRow())).toEqual({
        id: 'img-1',
        variantIndex: 1,
        status: 'Staged',
        mediaType: 'image/png',
        width: 1024,
        height: 1024,
        sizeBytes: 482000,
        retentionExpiresAt: '2026-10-09T12:00:00Z',
        createdAt: '2026-10-08T12:00:00Z',
      });
    });

    it('accepts the string form of every integer, which the contract also publishes', () => {
      const decoded = decodeStagedImage(
        stagedRow({ variantIndex: '2', width: '512', height: '768', sizeBytes: '12345' }),
      );

      expect(decoded?.variantIndex).toBe(2);
      expect(decoded?.width).toBe(512);
      expect(decoded?.height).toBe(768);
      expect(decoded?.sizeBytes).toBe(12345);
    });

    it('refuses a row with a status this build does not know', () => {
      expect(decodeStagedImage(stagedRow({ status: 'Quarantined' }))).toBeNull();
    });

    it('refuses a row missing anything the contract requires', () => {
      for (const field of ['id', 'mediaType', 'width', 'retentionExpiresAt', 'createdAt']) {
        const row = stagedRow();
        delete row[field];

        expect(decodeStagedImage(row)).withContext(field).toBeNull();
      }
    });
  });

  describe('decodeGeneratedImageOperation', () => {
    it('reads the accepted body, which carries no image identities at all', () => {
      const decoded = decodeGeneratedImageOperation(operationBody());

      expect(decoded?.id).toBe('op-1');
      expect(decoded?.status).toBe('Requested');
      expect(decoded?.variantCount).toBe(4);
      expect(Object.keys(decoded ?? {})).withContext('the 202 names no picture').not.toContain('images');
    });

    it('refuses a body whose status is not one of the seven', () => {
      expect(decodeGeneratedImageOperation(operationBody({ status: 'Pending' }))).toBeNull();
    });

    it('refuses anything that is not a record', () => {
      expect(decodeGeneratedImageOperation(null)).toBeNull();
      expect(decodeGeneratedImageOperation('op-1')).toBeNull();
    });
  });

  describe('decodeGeneratedImageOperationDetail', () => {
    it('reads a run whose pictures have not all landed yet', () => {
      const decoded = decodeGeneratedImageOperationDetail(
        operationBody({ status: 'Running', variantCount: 4, stagedCount: 2, images: [stagedRow(), stagedRow({ id: 'img-2', variantIndex: 2 })] }),
      );

      expect(decoded?.variantCount).toBe(4);
      expect(decoded?.images.length).withContext('fewer rows than variants is ordinary').toBe(2);
    });

    it('orders the pictures by variant whatever order they arrived in', () => {
      const decoded = decodeGeneratedImageOperationDetail(
        operationBody({
          images: [stagedRow({ id: 'img-3', variantIndex: 3 }), stagedRow({ id: 'img-1', variantIndex: 1 })],
        }),
      );

      expect(decoded?.images.map((image) => image.variantIndex)).toEqual([1, 3]);
    });

    it('drops one unreadable picture and keeps the rest, because the others are still pictures', () => {
      const decoded = decodeGeneratedImageOperationDetail(
        operationBody({ images: [stagedRow(), stagedRow({ id: 'img-2', status: 'Quarantined' })] }),
      );

      expect(decoded?.images.map((image) => image.id)).toEqual(['img-1']);
    });

    it('reads an empty picture list as nothing having landed yet', () => {
      expect(decodeGeneratedImageOperationDetail(operationBody({ images: [] }))?.images).toEqual([]);
    });

    it('refuses a body with no picture list, which claims a count it offers no way to reach', () => {
      expect(decodeGeneratedImageOperationDetail(operationBody())).toBeNull();
    });
  });

  describe('run and picture states', () => {
    it('treats PartiallySucceeded as finished and not as a failure, because some pictures landed', () => {
      expect(isGeneratedImageOperationFinished('PartiallySucceeded')).toBeTrue();
      expect(isGeneratedImageOperationFailed('PartiallySucceeded')).toBeFalse();
    });

    it('keeps watching only while there is something left to learn', () => {
      const watched: GeneratedImageOperationStatus[] = ['Unspecified', 'Requested', 'Running'];
      for (const status of watched) {
        expect(isGeneratedImageOperationFinished(status)).withContext(status).toBeFalse();
      }

      for (const status of ['Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled'] as const) {
        expect(isGeneratedImageOperationFinished(status)).withContext(status).toBeTrue();
      }
    });

    it('offers an action on a staged picture only, because every other state is terminal', () => {
      expect(isStagedImageActionable('Staged')).toBeTrue();
      for (const status of ['Unspecified', 'Kept', 'Rejected', 'Expired'] as const) {
        expect(isStagedImageActionable(status)).withContext(status).toBeFalse();
      }
    });
  });

  describe('failure sentences', () => {
    it('has a sentence for every category the server records', () => {
      for (const category of SERVER_FAILURE_CATEGORIES) {
        expect(GENERATED_IMAGE_FAILURE_SENTENCES[category]?.length)
          .withContext(category)
          .toBeGreaterThan(0);
      }
    });

    it('names no category the server does not, so the two cannot drift apart unnoticed', () => {
      expect(Object.keys(GENERATED_IMAGE_FAILURE_SENTENCES).sort()).toEqual(
        [...SERVER_FAILURE_CATEGORIES].sort(),
      );
    });

    it('tells a creator to ask again for exactly the failures a later attempt could get past', () => {
      for (const category of SERVER_RETRYABLE) {
        expect(GENERATED_IMAGE_FAILURE_SENTENCES[category].toLowerCase())
          .withContext(category)
          .toContain('asking again usually works');
      }
    });

    it('never names a provider, a model, a lease or storage in words a creator reads', () => {
      for (const [category, sentence] of Object.entries(GENERATED_IMAGE_FAILURE_SENTENCES)) {
        for (const word of ['provider', 'lease', 'worker', 'queue', 'blob', 'endpoint']) {
          expect(sentence.toLowerCase()).withContext(`${category} / ${word}`).not.toContain(word);
        }
      }
    });

    it('prefers the server’s own summary, which was written about this run', () => {
      expect(generatedImageFailureSentence('storage', 'None of your pictures could be put away.')).toBe(
        'None of your pictures could be put away.',
      );
    });

    it('falls back to the category when there is no summary', () => {
      expect(generatedImageFailureSentence('rate-limited', '   ')).toBe(
        GENERATED_IMAGE_FAILURE_SENTENCES['rate-limited'],
      );
    });

    it('still says something for a category this build has never heard of', () => {
      expect(generatedImageFailureSentence('something-new', null).length).toBeGreaterThan(0);
      expect(generatedImageFailureSentence(null, null).length).toBeGreaterThan(0);
    });
  });

  describe('encodeRequestGeneratedImages', () => {
    it('sends exactly the four fields the route takes, and no workspace or key among them', () => {
      const body = encodeRequestGeneratedImages({
        promptText: 'A tight crop, soft light.',
        avoidText: 'clutter',
        variantCount: 2,
      });

      expect(Object.keys(body).sort()).toEqual(['aiProposalId', 'avoidText', 'promptText', 'variantCount']);
      expect(body['promptText']).toBe('A tight crop, soft light.');
      expect(body['variantCount']).toBe(2);
    });

    it('sends no proposal id, because the server does not validate one and the draft holds no proposal', () => {
      const body = encodeRequestGeneratedImages({ promptText: 'A tight crop.', avoidText: null, variantCount: 1 });

      expect(body['aiProposalId']).toBeNull();
    });
  });

  describe('avoidTextFor', () => {
    it('joins the list the way every proposal row a client reads is joined', () => {
      expect(avoidTextFor(['clutter', 'harsh light'])).toBe('clutter; harsh light');
    });

    it('trims each entry and drops the ones a creator emptied', () => {
      expect(avoidTextFor([' clutter ', '', '   ', 'glare'])).toBe('clutter; glare');
    });

    it('answers null when there is nothing to avoid, so no empty field travels', () => {
      expect(avoidTextFor([])).toBeNull();
      expect(avoidTextFor(['  '])).toBeNull();
    });

    it('cuts to the column rather than refusing, because most of the list still says what was meant', () => {
      const long = avoidTextFor([`${'a'.repeat(GENERATED_IMAGE_PROMPT_MAX_LENGTH)}b`]);

      expect(long?.length).toBe(GENERATED_IMAGE_PROMPT_MAX_LENGTH);
    });
  });
});
