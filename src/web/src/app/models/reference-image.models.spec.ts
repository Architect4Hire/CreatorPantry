import { AiProposalDetail, AiProposedChange } from './ai-proposal.models';
import {
  decodeReferenceImageReading,
  encodeRequestReferenceImage,
  referenceReadingWarnings,
} from './reference-image.models';

function change(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'ReferenceImageAnalysis',
    targetId: 't1',
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function detail(changes: readonly AiProposedChange[]): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: '1',
    promptTemplateId: 'img-004',
    promptTemplateVersion: '1',
    promptTemplateBodyChecksum: 'abc',
    providerName: 'provider',
    modelName: 'model',
    createdAt: '2026-10-07T12:00:00Z',
    changes,
    warnings: [],
  };
}

const READING: readonly AiProposedChange[] = [
  change({ changeKind: 'Add', afterValue: 'A loaf on scrubbed oak in side light.', proposedPosition: 0 }),
  change({ fieldName: 'observation.Lighting', afterValue: 'Hard side light from the left' }),
  change({ fieldName: 'observation.Lighting.confidence', afterValue: 'Clear' }),
  change({ fieldName: 'observation.Subject', afterValue: 'A round loaf, cut' }),
  change({ fieldName: 'observation.Subject.confidence', afterValue: 'Probable' }),
  change({ fieldName: 'avoid', afterValue: 'hands; brand marks' }),
];

describe('reference-image.models', () => {
  it('reads the observations, each with its own confidence, and the prompt drawn from them', () => {
    const reading = decodeReferenceImageReading(detail(READING));

    expect(reading?.prompt).toBe('A loaf on scrubbed oak in side light.');
    expect(reading?.avoid).toEqual(['hands', 'brand marks']);
    expect(reading?.observations).toEqual([
      { aspect: 'Subject', text: 'A round loaf, cut', confidence: 'Probable' },
      { aspect: 'Lighting', text: 'Hard side light from the left', confidence: 'Clear' },
    ]);
  });

  it('reads an observation whose confidence it cannot understand as not stated, never as certain', () => {
    const reading = decodeReferenceImageReading(
      detail([
        change({ changeKind: 'Add', afterValue: 'A loaf.', proposedPosition: 0 }),
        change({ fieldName: 'observation.Mood', afterValue: 'Calm' }),
        change({ fieldName: 'observation.Mood.confidence', afterValue: 'VeryVeryCertain' }),
      ]),
    );

    expect(reading?.observations).toEqual([{ aspect: 'Mood', text: 'Calm', confidence: 'Unspecified' }]);
  });

  it('keeps an observation that came back with no confidence at all, marked as unstated', () => {
    const reading = decodeReferenceImageReading(
      detail([
        change({ changeKind: 'Add', afterValue: 'A loaf.', proposedPosition: 0 }),
        change({ fieldName: 'observation.Colour', afterValue: 'Warm and low in contrast' }),
      ]),
    );

    expect(reading?.observations).toEqual([
      { aspect: 'Colour', text: 'Warm and low in contrast', confidence: 'Unspecified' },
    ]);
  });

  it('skips an aspect that came back blank rather than listing an empty reading', () => {
    const reading = decodeReferenceImageReading(
      detail([
        change({ changeKind: 'Add', afterValue: 'A loaf.', proposedPosition: 0 }),
        change({ fieldName: 'observation.Styling', afterValue: '   ' }),
      ]),
    );

    expect(reading?.observations).toEqual([]);
  });

  it('reads a prompt with no observations at all, which is a reading that saw nothing it was sure of', () => {
    const reading = decodeReferenceImageReading(
      detail([change({ changeKind: 'Add', afterValue: 'A loaf.', proposedPosition: 0 })]),
    );

    expect(reading?.prompt).toBe('A loaf.');
    expect(reading?.observations).toEqual([]);
  });

  it('is null when there is no proposal, and when the proposal holds no prompt', () => {
    expect(decodeReferenceImageReading(null)).toBeNull();
    expect(decodeReferenceImageReading(detail([change({ fieldName: 'avoid', afterValue: 'hands' })]))).toBeNull();
  });

  it('reads the warnings even when there is no reading to hang them off', () => {
    const warned: AiProposalDetail = {
      ...detail([change({ fieldName: 'avoid', afterValue: 'hands' })]),
      warnings: [{ kind: 'Limitation', message: 'That photograph could not be read.', changeId: null }],
    };

    expect(decodeReferenceImageReading(warned)).toBeNull();
    expect(referenceReadingWarnings(warned).map((warning) => warning.message)).toEqual([
      'That photograph could not be read.',
    ]);
    expect(referenceReadingWarnings(null)).toEqual([]);
  });

  it('sends a Brand Library document as the body it always was, and the note only when there is one', () => {
    const picture = { source: 'BrandDocument', referenceDocumentId: 'd1' } as const;

    // No `source`: an older server, and this one, both read a lone document id as a brand document.
    expect(encodeRequestReferenceImage({ picture, note: '  ' })).toEqual({ referenceDocumentId: 'd1' });
    expect(encodeRequestReferenceImage({ picture, note: ' the light ' })).toEqual({
      referenceDocumentId: 'd1',
      note: 'the light',
    });
  });

  it('names a library picture by its source, its id and the version it was chosen at', () => {
    expect(
      encodeRequestReferenceImage({
        picture: { source: 'DamAsset', mediaAssetId: 'a1', mediaAssetVersionNumber: 3 },
        note: '',
      }),
    ).toEqual({ source: 'DamAsset', mediaAssetId: 'a1', mediaAssetVersionNumber: 3 });

    // No version chosen: none is sent, and the server reads the current one.
    expect(
      encodeRequestReferenceImage({
        picture: { source: 'DamAsset', mediaAssetId: 'a1', mediaAssetVersionNumber: null },
        note: '',
      }),
    ).toEqual({ source: 'DamAsset', mediaAssetId: 'a1' });
  });

  it('names a generated picture by its source and id, and never another source’s id beside it', () => {
    const body = encodeRequestReferenceImage({
      picture: { source: 'GeneratedImage', generatedImageId: 'g1' },
      note: 'the crop',
    });

    expect(body).toEqual({ source: 'GeneratedImage', generatedImageId: 'g1', note: 'the crop' });
    expect('referenceDocumentId' in body).toBeFalse();
    expect('mediaAssetId' in body).toBeFalse();
  });
});
