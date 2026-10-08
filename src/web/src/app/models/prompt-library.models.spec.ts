import { ContentChannel } from './brand-profile.models';
import { isImageStudioDraftEmpty } from './image-studio.models';
import {
  PromptDetail,
  decodePromptDetail,
  decodePromptSearchPage,
  decodePromptSummary,
  encodePromptSearchQuery,
  imageStudioDraftFromPrompt,
  isPromptPreviewTruncated,
  promptChannelName,
  promptTitle,
} from './prompt-library.models';

const ID = '0f4b2c9a-1111-4222-8333-444455556666';

/** A row exactly as `PromptSummaryServiceModel` serializes. */
function summaryBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    promptRecordId: ID,
    channelKey: 'instagram',
    imageKind: 'Hero',
    textPreview: 'A tight crop of a bowl of chili',
    textLength: 31,
    label: 'Chili hero',
    source: 'ImagePromptComposition',
    recipeId: null,
    recipeVersionId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

/** A prompt exactly as `PromptDetailServiceModel` serializes. */
function detailBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    promptRecordId: ID,
    channelKey: 'instagram',
    imageKind: 'Hero',
    text: 'A tight crop of a bowl of chili, soft window light.',
    generatedText: 'A bowl of chili.',
    label: 'Chili hero',
    source: 'ImagePromptComposition',
    aiProposalId: 'aa11bb22-3333-4444-8555-666677778888',
    recipeId: null,
    recipeVersionId: null,
    promptTemplateId: 'image.prompt',
    promptTemplateVersion: '1.2.0',
    promptTemplateBodyChecksum: 'abc123',
    createdAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'vine', displayName: 'Vine', isActive: false },
];

describe('prompt library models', () => {
  describe('decodePromptSummary', () => {
    it('reads a row the server sends', () => {
      expect(decodePromptSummary(summaryBody())).toEqual({
        promptRecordId: ID,
        channelKey: 'instagram',
        imageKind: 'Hero',
        textPreview: 'A tight crop of a bowl of chili',
        textLength: 31,
        label: 'Chili hero',
        source: 'ImagePromptComposition',
        recipeId: null,
        recipeVersionId: null,
        createdAt: '2026-10-08T12:00:00+00:00',
      });
    });

    it('carries nothing the contract does not name, so a stray location could never reach a screen', () => {
      const decoded = decodePromptSummary(
        summaryBody({ storagePath: 'prompts/w1/a.txt', blobUrl: 'https://x/y', workspaceId: 'w1' }),
      );

      expect(Object.keys(decoded ?? {})).not.toContain('storagePath');
      expect(Object.keys(decoded ?? {})).not.toContain('blobUrl');
      expect(Object.keys(decoded ?? {})).not.toContain('workspaceId');
    });

    it('refuses an image kind or source this build does not know, rather than guessing one', () => {
      expect(decodePromptSummary(summaryBody({ imageKind: 'Hologram' }))).toBeNull();
      expect(decodePromptSummary(summaryBody({ source: 7 }))).toBeNull();
    });

    it('refuses a row missing its id, preview, length or date', () => {
      expect(decodePromptSummary(summaryBody({ promptRecordId: '' }))).toBeNull();
      expect(decodePromptSummary(summaryBody({ textPreview: null }))).toBeNull();
      expect(decodePromptSummary(summaryBody({ textLength: '31' }))).toBeNull();
      expect(decodePromptSummary(summaryBody({ createdAt: undefined }))).toBeNull();
      expect(decodePromptSummary(null)).toBeNull();
    });
  });

  describe('decodePromptSearchPage', () => {
    it('reads a page with its cursor and total', () => {
      const page = decodePromptSearchPage({ items: [summaryBody()], nextCursor: 'c1', totalCount: 12 });

      expect(page?.items.length).toBe(1);
      expect(page?.nextCursor).toBe('c1');
      expect(page?.totalCount).toBe(12);
    });

    it('reads the last page, and a page that was asked not to count', () => {
      expect(decodePromptSearchPage({ items: [], nextCursor: null, totalCount: null })).toEqual({
        items: [],
        nextCursor: null,
        totalCount: null,
      });
    });

    it('fails the whole page on one unreadable row instead of quietly shortening it', () => {
      expect(
        decodePromptSearchPage({ items: [summaryBody(), { promptRecordId: ID }], nextCursor: null, totalCount: 2 }),
      ).toBeNull();
    });

    it('refuses a body that is not a page', () => {
      expect(decodePromptSearchPage({ items: 'none', nextCursor: null, totalCount: 0 })).toBeNull();
      expect(decodePromptSearchPage({ items: [], nextCursor: 4, totalCount: 0 })).toBeNull();
      expect(decodePromptSearchPage([])).toBeNull();
    });
  });

  describe('decodePromptDetail', () => {
    it('reads a prompt in full', () => {
      const detail = decodePromptDetail(detailBody());

      expect(detail?.text).toBe('A tight crop of a bowl of chili, soft window light.');
      expect(detail?.generatedText).toBe('A bowl of chili.');
      expect(detail?.promptTemplateVersion).toBe('1.2.0');
    });

    it('reads a manual prompt, which has no draft, proposal or template', () => {
      const detail = decodePromptDetail(
        detailBody({
          source: 'Manual',
          generatedText: null,
          aiProposalId: null,
          promptTemplateId: null,
          promptTemplateVersion: null,
          promptTemplateBodyChecksum: null,
          label: null,
        }),
      );

      expect(detail?.source).toBe('Manual');
      expect(detail?.generatedText).toBeNull();
      expect(detail?.label).toBeNull();
    });

    it('refuses a body without the prompt text', () => {
      expect(decodePromptDetail(detailBody({ text: null }))).toBeNull();
      expect(decodePromptDetail('nope')).toBeNull();
    });
  });

  describe('encodePromptSearchQuery', () => {
    it('sends nothing for an unfiltered first page, and never a sort', () => {
      expect(encodePromptSearchQuery({ search: '', channel: null, cursor: null })).toEqual({});
    });

    it('sends the trimmed search and the channel', () => {
      expect(encodePromptSearchQuery({ search: '  chili ', channel: 'instagram', cursor: null })).toEqual({
        search: 'chili',
        channel: 'instagram',
      });
    });

    it('asks not to be counted again when following a cursor', () => {
      expect(encodePromptSearchQuery({ search: '', channel: null, cursor: 'c1' })).toEqual({
        cursor: 'c1',
        includeTotal: 'false',
      });
    });
  });

  describe('view helpers', () => {
    it('names a prompt without a label', () => {
      expect(promptTitle(null)).toBe('Untitled prompt');
      expect(promptTitle('   ')).toBe('Untitled prompt');
      expect(promptTitle(' Chili hero ')).toBe('Chili hero');
    });

    it('claims truncation only when the full prompt is longer than its preview', () => {
      const whole = decodePromptSummary(summaryBody({ textPreview: 'abc', textLength: 3 }))!;
      const cut = decodePromptSummary(summaryBody({ textPreview: 'abc', textLength: 300 }))!;

      expect(isPromptPreviewTruncated(whole)).toBeFalse();
      expect(isPromptPreviewTruncated(cut)).toBeTrue();
    });

    it("shows a channel's name, and the key itself for one the catalogue does not hold", () => {
      expect(promptChannelName(CHANNELS, 'instagram')).toBe('Instagram');
      expect(promptChannelName(CHANNELS, 'myspace')).toBe('myspace');
      expect(promptChannelName([], 'instagram')).toBe('instagram');
    });
  });

  describe('imageStudioDraftFromPrompt', () => {
    const detail = decodePromptDetail(detailBody()) as PromptDetail;

    it("starts a new draft holding a copy of the prompt, as the creator's own words", () => {
      const draft = imageStudioDraftFromPrompt(detail, CHANNELS, new Date('2026-10-08T13:00:00Z'));

      expect(draft.prompt.finalPrompt).toBe(detail.text);
      // 'creator', so nothing in the studio replaces it without asking.
      expect(draft.prompt.promptSource).toBe('creator');
      expect(draft.config.channelKey).toBe('instagram');
      expect(draft.savedAt).toBe('2026-10-08T13:00:00.000Z');
      expect(isImageStudioDraftEmpty(draft)).toBeFalse();
    });

    it('carries no request, pick or picture over — it is new work, not the old run', () => {
      const draft = imageStudioDraftFromPrompt(detail, CHANNELS);

      expect(draft.prompt.promptRequestId).toBeNull();
      expect(draft.prompt.conceptRequestId).toBeNull();
      expect(draft.prompt.chosen).toBeNull();
      expect(draft.prompt.generated).toBeNull();
      expect(draft.images).toEqual({ operationId: null, keepers: [] });
    });

    it('leaves the channel unset when it is retired or unknown, since the studio could not use it', () => {
      const retired = { ...detail, channelKey: 'vine' };
      const unknown = { ...detail, channelKey: 'myspace' };

      expect(imageStudioDraftFromPrompt(retired, CHANNELS).config.channelKey).toBeNull();
      expect(imageStudioDraftFromPrompt(unknown, CHANNELS).config.channelKey).toBeNull();
      expect(imageStudioDraftFromPrompt(detail, []).config.channelKey).toBeNull();
    });

    it('does not change the prompt it was made from', () => {
      const before = JSON.stringify(detail);
      imageStudioDraftFromPrompt(detail, CHANNELS);

      expect(JSON.stringify(detail)).toBe(before);
    });
  });
});
