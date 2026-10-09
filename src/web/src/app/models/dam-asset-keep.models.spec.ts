import {
  DamCreatedAsset,
  DamKeepDraft,
  DamKeepPrompt,
  damKeepFieldFor,
  decodeDamCreatedAsset,
  encodeDamKeep,
  isDamKeepRepeat,
  sameDamKeepDraft,
  validateDamKeepDraft,
} from './dam-asset.models';

function draft(overrides: Partial<DamKeepDraft> = {}): DamKeepDraft {
  return { title: 'Soda bread hero', altText: '', tagIds: [], recipe: null, keepPrompt: false, ...overrides };
}

function asset(overrides: Partial<DamCreatedAsset> = {}): DamCreatedAsset {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    kind: 'AiGenerated',
    currentVersionNumber: 1,
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 2048,
    sourceGeneratedImageId: 'g1',
    promptRecordId: null,
    recipeId: null,
    createdAt: '2026-10-09T12:00:00+00:00',
    ...overrides,
  };
}

const PROMPT: DamKeepPrompt = {
  channelKey: 'instagram',
  imageKind: 'Hero',
  text: 'Overhead shot of soda bread on linen.',
  source: 'Manual',
};

describe('DAM keep models', () => {
  describe('decodeDamCreatedAsset', () => {
    it('reads the asset field by field, and reads absent lineage as none', () => {
      const { promptRecordId: _p, recipeId: _r, ...rest } = asset();

      expect(decodeDamCreatedAsset({ ...rest, objectKey: 'never/carried' })).toEqual(asset());
    });

    it('refuses a reply with no id, an unknown kind, or a missing media fact', () => {
      expect(decodeDamCreatedAsset({ ...asset(), id: '' })).toBeNull();
      expect(decodeDamCreatedAsset({ ...asset(), kind: 'Unspecified' })).toBeNull();
      expect(decodeDamCreatedAsset({ ...asset(), width: '1024' })).toBeNull();
      expect(decodeDamCreatedAsset(null)).toBeNull();
    });
  });

  describe('validateDamKeepDraft', () => {
    it('needs a title and nothing else', () => {
      expect(validateDamKeepDraft(draft())).toEqual({});
      expect(validateDamKeepDraft(draft({ title: '   ' })).title).toContain('needs a title');
    });

    it("holds the title, alt text and tags to the server's limits", () => {
      const errors = validateDamKeepDraft(
        draft({
          title: 'x'.repeat(201),
          altText: 'x'.repeat(1001),
          tagIds: Array.from({ length: 26 }, (_, index) => `t${index}`),
        }),
      );

      expect(errors.title).toContain('200');
      expect(errors.altText).toContain('1000');
      expect(errors.tagIds).toContain('25');
    });
  });

  describe('encodeDamKeep', () => {
    it('sends the picture and a trimmed title, and leaves out everything that is empty', () => {
      expect(encodeDamKeep('g1', draft({ title: ' Soda bread hero ', altText: '  ' }), null)).toEqual({
        generatedImageId: 'g1',
        metadata: { title: 'Soda bread hero' },
      });
    });

    it('sends alt text, tags and the recipe link as the route names them', () => {
      expect(
        encodeDamKeep(
          'g1',
          draft({ altText: ' A round loaf. ', tagIds: ['t1', 't2'], recipe: { id: 'r1', title: 'Soda bread' } }),
          null,
        ),
      ).toEqual({
        generatedImageId: 'g1',
        metadata: { title: 'Soda bread hero', altText: 'A round loaf.', workspaceTagIds: ['t1', 't2'] },
        recipeLink: { recipeId: 'r1' },
      });
    });

    it('sends the prompt only when it is to be kept, naming the picture it made', () => {
      expect(encodeDamKeep('g1', draft({ keepPrompt: false }), PROMPT)['prompt']).toBeUndefined();
      expect(encodeDamKeep('g1', draft({ keepPrompt: true }), null)['prompt']).toBeUndefined();
      expect(encodeDamKeep('g1', draft({ keepPrompt: true }), PROMPT)['prompt']).toEqual({
        ...PROMPT,
        generatedImageId: 'g1',
      });
    });

    it("carries a generated prompt's lineage and drops what it does not have", () => {
      const sent = encodeDamKeep(
        'g1',
        draft({ keepPrompt: true }),
        { ...PROMPT, source: 'ImagePromptComposition', aiProposalId: 'p9', generatedText: 'Draft.', label: null, recipeId: '' },
      )['prompt'] as Record<string, unknown>;

      expect(sent['aiProposalId']).toBe('p9');
      expect(sent['generatedText']).toBe('Draft.');
      expect(Object.keys(sent)).not.toContain('label');
      expect(Object.keys(sent)).not.toContain('recipeId');
    });

    it('never names a workspace or anything about the bytes', () => {
      const body = JSON.stringify(encodeDamKeep('g1', draft({ keepPrompt: true }), PROMPT));

      for (const word of ['workspace', 'mediaType', 'width', 'objectKey', 'checksum']) {
        expect(body).withContext(word).not.toContain(word);
      }
    });
  });

  describe('damKeepFieldFor', () => {
    it("maps the server's names, in either case and nested or not, to the dialog's fields", () => {
      expect(damKeepFieldFor('Title')).toBe('title');
      expect(damKeepFieldFor('metadata.altText')).toBe('altText');
      expect(damKeepFieldFor('WorkspaceTagIds')).toBe('tagIds');
      expect(damKeepFieldFor('recipeId')).toBe('recipe');
      expect(damKeepFieldFor('GeneratedImageId')).toBeNull();
      expect(damKeepFieldFor('CuisineId')).toBeNull();
    });
  });

  describe('sameDamKeepDraft', () => {
    it('ignores surrounding space and tag order, and notices every real change', () => {
      const base = draft({ tagIds: ['t1', 't2'], recipe: { id: 'r1', title: 'Soda bread' } });

      expect(sameDamKeepDraft(base, { ...base, title: ' Soda bread hero ', tagIds: ['t2', 't1'] })).toBeTrue();
      expect(sameDamKeepDraft(base, { ...base, altText: 'A loaf.' })).toBeFalse();
      expect(sameDamKeepDraft(base, { ...base, tagIds: ['t1'] })).toBeFalse();
      expect(sameDamKeepDraft(base, { ...base, recipe: null })).toBeFalse();
      expect(sameDamKeepDraft(base, { ...base, keepPrompt: true })).toBeFalse();
    });
  });

  describe('isDamKeepRepeat', () => {
    it('reads a first save as a first save', () => {
      expect(isDamKeepRepeat(draft(), false, asset())).toBeFalse();
      expect(isDamKeepRepeat(draft(), true, asset({ promptRecordId: 'p1' }))).toBeFalse();
    });

    it('reads an answer under another title, or with no record of the prompt sent, as the earlier asset', () => {
      expect(isDamKeepRepeat(draft({ title: 'A new name' }), false, asset())).toBeTrue();
      expect(isDamKeepRepeat(draft(), true, asset({ promptRecordId: null }))).toBeTrue();
    });
  });
});
