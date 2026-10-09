import { AiProposalDetail, AiProposedChange } from './ai-proposal.models';
import {
  composedPromptWarnings,
  decodeComposedImagePrompt,
  encodeRequestImagePrompt,
} from './image-prompt.models';

function detail(changes: readonly AiProposedChange[]): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: '1',
    promptTemplateId: 'img-002',
    promptTemplateVersion: '1',
    promptTemplateBodyChecksum: 'abc',
    providerName: 'provider',
    modelName: 'model',
    createdAt: '2026-10-07T12:00:00Z',
    changes,
    warnings: [{ kind: 'Limitation', message: 'No brief was readable.', changeId: null }],
  };
}

function change(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'ImagePrompt',
    targetId: 't1',
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

describe('image-prompt.models', () => {
  it('reads the composed prompt and its avoid list', () => {
    const composed = decodeComposedImagePrompt(
      detail([
        change({ changeKind: 'Add', afterValue: 'A three-quarter view of a sourdough loaf.', proposedPosition: 0 }),
        change({ fieldName: 'avoid', afterValue: 'hands; text overlay' }),
      ]),
    );

    expect(composed?.prompt).toBe('A three-quarter view of a sourdough loaf.');
    expect(composed?.avoid).toEqual(['hands', 'text overlay']);
  });

  it('reads a prompt with nothing to avoid', () => {
    const composed = decodeComposedImagePrompt(
      detail([change({ changeKind: 'Add', afterValue: 'A loaf.', proposedPosition: 0 })]),
    );

    expect(composed?.avoid).toEqual([]);
  });

  it('reads the warnings even when there is no prompt to hang them off', () => {
    // A proposal can carry the Limitation that explains why nothing usable came back. Reading warnings through
    // the prompt would drop exactly those.
    const onlyAvoid = detail([change({ fieldName: 'avoid', afterValue: 'hands' })]);

    expect(decodeComposedImagePrompt(onlyAvoid)).toBeNull();
    expect(composedPromptWarnings(onlyAvoid).map((warning) => warning.message)).toEqual([
      'No brief was readable.',
    ]);
  });

  it('has no warnings when there is no proposal at all', () => {
    expect(composedPromptWarnings(null)).toEqual([]);
  });

  it('is null when there is no proposal, and when the proposal holds no prompt', () => {
    expect(decodeComposedImagePrompt(null)).toBeNull();
    expect(decodeComposedImagePrompt(detail([change({ fieldName: 'avoid', afterValue: 'hands' })]))).toBeNull();
  });

  it('sends the concept, its shot and nothing it was not given', () => {
    expect(
      encodeRequestImagePrompt({
        conceptRequestId: 'r1',
        conceptId: 'k1',
        shotKind: 'Hero',
        channelKey: null,
        briefDocumentId: null,
        sceneOverrides: [],
        styleOverrides: [],
      }),
    ).toEqual({ conceptRequestId: 'r1', conceptId: 'k1', shotKind: 'Hero' });
  });

  it('names the linked recipe and its pinned version, and neither for an unlinked run', () => {
    const base = {
      conceptRequestId: 'r1',
      conceptId: 'k1',
      shotKind: 'Hero' as const,
      channelKey: null,
      briefDocumentId: null,
      sceneOverrides: [],
      styleOverrides: [],
    };

    expect(encodeRequestImagePrompt({ ...base, recipeId: 'r-soda', recipeVersionId: 'v-soda-2' })).toEqual({
      conceptRequestId: 'r1',
      conceptId: 'k1',
      shotKind: 'Hero',
      recipeId: 'r-soda',
      recipeVersionId: 'v-soda-2',
    });
    expect(encodeRequestImagePrompt({ ...base, recipeId: 'r-new', recipeVersionId: null })).toEqual({
      conceptRequestId: 'r1',
      conceptId: 'k1',
      shotKind: 'Hero',
      recipeId: 'r-new',
    });

    const unlinked = encodeRequestImagePrompt({ ...base, recipeId: null, recipeVersionId: null });
    expect('recipeId' in unlinked).toBeFalse();
    expect('recipeVersionId' in unlinked).toBeFalse();
    // A version without its recipe is refused by the route, so it is never sent.
    expect(encodeRequestImagePrompt({ ...base, recipeId: null, recipeVersionId: 'v-soda-2' })).toEqual(
      encodeRequestImagePrompt(base),
    );
  });

  it('sends the brief and the overrides when there are any', () => {
    expect(
      encodeRequestImagePrompt({
        conceptRequestId: 'r1',
        conceptId: 'k1',
        shotKind: 'DetailShot',
        channelKey: 'instagram',
        briefDocumentId: 'd1',
        sceneOverrides: ['marble slab'],
        styleOverrides: ['soft light'],
      }),
    ).toEqual({
      conceptRequestId: 'r1',
      conceptId: 'k1',
      shotKind: 'DetailShot',
      channelKey: 'instagram',
      briefDocumentId: 'd1',
      sceneOverrides: ['marble slab'],
      styleOverrides: ['soft light'],
    });
  });
});
