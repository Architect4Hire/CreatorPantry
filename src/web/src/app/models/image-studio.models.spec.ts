import {
  IMAGE_STUDIO_DRAFT_VERSION,
  ImageStudioDraft,
  decodeImageStudioDraft,
  emptyImageStudioDraft,
  encodeImageStudioDraft,
  isImageStudioDraftEmpty,
} from './image-studio.models';

function filled(): ImageStudioDraft {
  const base = emptyImageStudioDraft(new Date('2026-10-08T12:00:00Z'));

  return {
    ...base,
    config: { ...base.config, channelKey: 'instagram', variantCount: 3, scene: ['a linen cloth'], concept: 'A tight crop.' },
    prompt: {
      ...base.prompt,
      conceptRequestId: 'r-concept',
      chosen: { conceptRequestId: 'r-concept', conceptId: 'k-1', label: 'Warm morning', shotKind: 'Hero' },
      generated: { text: 'Soft light.', avoid: ['clutter'] },
      finalPrompt: 'Soft light, my way.',
      promptSource: 'creator',
    },
    images: { operationId: 'op-1' },
  };
}

describe('image studio draft', () => {
  it('reads back what it wrote', () => {
    const draft = filled();

    expect(decodeImageStudioDraft(encodeImageStudioDraft(draft))).toEqual(draft);
  });

  it('starts empty, and knows it', () => {
    expect(isImageStudioDraftEmpty(emptyImageStudioDraft())).toBeTrue();
    expect(isImageStudioDraftEmpty(filled())).toBeFalse();
  });

  it('counts a typed prompt alone as something to lose', () => {
    const base = emptyImageStudioDraft();

    expect(
      isImageStudioDraftEmpty({ ...base, prompt: { ...base.prompt, finalPrompt: 'Mine.', promptSource: 'creator' } }),
    ).toBeFalse();
  });

  it('answers null for nothing, for something that is not JSON, and for something that is not a draft', () => {
    expect(decodeImageStudioDraft(null)).toBeNull();
    expect(decodeImageStudioDraft('')).toBeNull();
    expect(decodeImageStudioDraft('{not json')).toBeNull();
    expect(decodeImageStudioDraft('[]')).toBeNull();
  });

  it('discards a draft from another version rather than guessing at it', () => {
    const other = JSON.stringify({ ...filled(), v: IMAGE_STUDIO_DRAFT_VERSION + 1 });

    expect(decodeImageStudioDraft(other)).toBeNull();
  });

  it("discards a Content Pipeline draft, which is another screen's work in another shape", () => {
    const pipeline = JSON.stringify({ v: 4, ...filled(), seed: { lastToken: null, keep: {}, accepted: null } });

    expect(decodeImageStudioDraft(pipeline)).toBeNull();
  });

  it('discards a draft whose prompt block is malformed, rather than resuming with a different prompt', () => {
    const broken = JSON.stringify({ v: IMAGE_STUDIO_DRAFT_VERSION, ...filled(), prompt: { finalPrompt: 7 } });

    expect(decodeImageStudioDraft(broken)).toBeNull();
  });

  it('discards a draft too large to be one this screen wrote', () => {
    const base = filled();
    const huge = encodeImageStudioDraft({ ...base, prompt: { ...base.prompt, finalPrompt: 'x'.repeat(70 * 1024) } });

    expect(decodeImageStudioDraft(huge)).toBeNull();
  });

  it('reads an unrecognised prompt source on a non-empty prompt as the creator\'s own words', () => {
    const base = filled();
    const raw = JSON.stringify({
      v: IMAGE_STUDIO_DRAFT_VERSION,
      ...base,
      prompt: { ...base.prompt, promptSource: 'something-new' },
    });

    expect(decodeImageStudioDraft(raw)?.prompt.promptSource).toBe('creator');
  });

  it('carries no day, even when one was stored', () => {
    const base = filled();
    const raw = JSON.stringify({ v: IMAGE_STUDIO_DRAFT_VERSION, ...base, config: { ...base.config, day: 'Wednesday' } });

    expect(decodeImageStudioDraft(raw)?.config.day).toBeNull();
  });

  it('drops keepers that name no run', () => {
    const raw = JSON.stringify({
      v: IMAGE_STUDIO_DRAFT_VERSION,
      ...filled(),
      images: { operationId: null, keepers: ['img-1'] },
    });

    // A marks list from an older shape is dropped rather than failing the draft (AF.4.3).
    expect(decodeImageStudioDraft(raw)?.images).toEqual({ operationId: null });
  });

  it('stores nothing about a picture, a provider or a proposal — ids and decisions only', () => {
    const raw = encodeImageStudioDraft(filled());

    for (const field of ['mediaType', 'sizeBytes', 'width', 'providerName', 'modelName', 'retentionExpiresAt', 'proposal', 'workspaceId']) {
      expect(raw).withContext(field).not.toContain(field);
    }
  });
});
