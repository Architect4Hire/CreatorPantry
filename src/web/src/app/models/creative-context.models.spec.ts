import {
  CreativeContextSource,
  creativeContextSourceKey,
  decodeCreativeContext,
  decodeCreativeContextPage,
  decodeCreativeContextReference,
  encodeCreativeContextDraft,
  encodeCreativeContextPatch,
  encodeCreativeContextSource,
} from './creative-context.models';

const ID = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11';
const OTHER = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a12';

function referenceBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: OTHER,
    kind: 'Recipe',
    sortOrder: 0,
    recipeId: ID,
    recipeVersionId: null,
    conceptRequestId: null,
    conceptId: null,
    mediaAssetId: null,
    mediaAssetVersionNumber: null,
    generatedImageId: null,
    promptRecordId: null,
    addedAt: '2026-10-09T12:00:00+00:00',
    ...overrides,
  };
}

function contextBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: ID,
    workingTitle: 'Soda bread, autumn',
    pictureBrief: 'Overhead, the loaf torn open on linen.',
    channelKeys: ['blog', 'instagram'],
    day: 'Monday',
    weeklyThemeKey: 'meat-free-monday',
    references: [referenceBody()],
    createdAt: '2026-10-09T12:00:00+00:00',
    updatedAt: '2026-10-09T12:05:00+00:00',
    archivedAt: null,
    concurrencyToken: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

describe('creative context models', () => {
  describe('decodeCreativeContext', () => {
    it('reads a context as the route publishes it', () => {
      const context = decodeCreativeContext(contextBody());

      expect(context).toEqual({
        id: ID,
        workingTitle: 'Soda bread, autumn',
        pictureBrief: 'Overhead, the loaf torn open on linen.',
        briefSource: null,
        workingBrief: null,
        channelKeys: ['blog', 'instagram'],
        day: 'Monday',
        weeklyThemeKey: 'meat-free-monday',
        references: [jasmine.objectContaining({ id: OTHER, kind: 'Recipe', recipeId: ID })],
        createdAt: '2026-10-09T12:00:00+00:00',
        updatedAt: '2026-10-09T12:05:00+00:00',
        archivedAt: null,
        concurrencyToken: 'AAAAAAAAB9E=',
      });
    });

    it('reads a context that has nothing but an id', () => {
      const context = decodeCreativeContext(
        contextBody({ workingTitle: null, pictureBrief: null, channelKeys: [], day: null, weeklyThemeKey: null, references: [] }),
      );

      expect(context?.workingTitle).toBeNull();
      expect(context?.day).toBeNull();
      expect(context?.references).toEqual([]);
    });

    it('carries nothing through that it was not asked to read', () => {
      const context = decodeCreativeContext(
        contextBody({ workspaceId: OTHER, createdByMembershipId: OTHER, rowVersion: 'AAAA' }),
      );

      expect(Object.keys(context!)).not.toContain('workspaceId');
      expect(Object.keys(context!)).not.toContain('createdByMembershipId');
      expect(Object.keys(context!)).not.toContain('rowVersion');
    });

    it('puts the references in their stored order whatever order they arrived in', () => {
      const context = decodeCreativeContext(
        contextBody({
          references: [
            referenceBody({ id: 'c', sortOrder: 4 }),
            referenceBody({ id: 'a', sortOrder: 0 }),
            referenceBody({ id: 'b', sortOrder: 2 }),
          ],
        }),
      );

      expect(context?.references.map((reference) => reference.id)).toEqual(['a', 'b', 'c']);
    });

    for (const [name, body] of [
      ['not an object', 'a string'],
      ['no id', contextBody({ id: undefined })],
      ['no concurrency token', contextBody({ concurrencyToken: undefined })],
      ['an empty concurrency token', contextBody({ concurrencyToken: '' })],
      ['a day that is not one', contextBody({ day: 'Someday' })],
      ['channel keys that are not strings', contextBody({ channelKeys: ['blog', 7] })],
      ['a title that is not text', contextBody({ workingTitle: 12 })],
      ['references that are not a list', contextBody({ references: null })],
    ] as const) {
      it(`refuses ${name}`, () => {
        expect(decodeCreativeContext(body)).toBeNull();
      });
    }

    it('refuses the whole context when one reference cannot be read', () => {
      // Dropping the bad one would show a piece of work naming fewer sources than it does, and the next edit
      // would be composed against that.
      const context = decodeCreativeContext(
        contextBody({ references: [referenceBody(), referenceBody({ kind: 'Hologram' })] }),
      );

      expect(context).toBeNull();
    });
  });

  describe('decodeCreativeContextReference', () => {
    it('reads each kind the seam accepts', () => {
      for (const kind of ['Recipe', 'RecipeConcept', 'DamAsset', 'GeneratedImage', 'PromptRecord']) {
        expect(decodeCreativeContextReference(referenceBody({ kind }))?.kind).toBe(kind as never);
      }
    });

    it('reads a pinned asset version as a number', () => {
      const reference = decodeCreativeContextReference(
        referenceBody({ kind: 'DamAsset', recipeId: null, mediaAssetId: ID, mediaAssetVersionNumber: 3 }),
      );

      expect(reference?.mediaAssetVersionNumber).toBe(3);
    });

    it('refuses a kind this client does not know, rather than guessing at its ids', () => {
      expect(decodeCreativeContextReference(referenceBody({ kind: 'SocialPackage' }))).toBeNull();
    });

    it('refuses a reference with no position', () => {
      expect(decodeCreativeContextReference(referenceBody({ sortOrder: undefined }))).toBeNull();
    });
  });

  describe('decodeCreativeContextPage', () => {
    const summary = {
      id: ID,
      workingTitle: 'Soda bread, autumn',
      channelKeys: ['blog'],
      day: null,
      weeklyThemeKey: null,
      referenceCount: 2,
      updatedAt: '2026-10-09T12:05:00+00:00',
    };

    it('reads a page and its cursor', () => {
      const page = decodeCreativeContextPage({ items: [summary], nextCursor: 'abc' });

      expect(page?.items).toEqual([summary]);
      expect(page?.nextCursor).toBe('abc');
    });

    it('reads the last page, which has no cursor', () => {
      expect(decodeCreativeContextPage({ items: [], nextCursor: null })).toEqual({ items: [], nextCursor: null });
    });

    it('refuses a page with a row it cannot read', () => {
      expect(decodeCreativeContextPage({ items: [summary, { id: ID }], nextCursor: null })).toBeNull();
    });
  });

  describe('encodeCreativeContextSource', () => {
    const cases: readonly (readonly [CreativeContextSource, Record<string, unknown>])[] = [
      [{ kind: 'Recipe', recipeId: ID }, { kind: 'Recipe', recipeId: ID }],
      [{ kind: 'Recipe', recipeId: ID, recipeVersionId: OTHER }, { kind: 'Recipe', recipeId: ID, recipeVersionId: OTHER }],
      [{ kind: 'Recipe', recipeId: ID, recipeVersionId: null }, { kind: 'Recipe', recipeId: ID }],
      [
        { kind: 'RecipeConcept', conceptRequestId: ID, conceptId: OTHER },
        { kind: 'RecipeConcept', conceptRequestId: ID, conceptId: OTHER },
      ],
      [{ kind: 'DamAsset', mediaAssetId: ID }, { kind: 'DamAsset', mediaAssetId: ID }],
      [
        { kind: 'DamAsset', mediaAssetId: ID, mediaAssetVersionNumber: 2 },
        { kind: 'DamAsset', mediaAssetId: ID, mediaAssetVersionNumber: 2 },
      ],
      [{ kind: 'GeneratedImage', generatedImageId: ID }, { kind: 'GeneratedImage', generatedImageId: ID }],
      [{ kind: 'PromptRecord', promptRecordId: ID }, { kind: 'PromptRecord', promptRecordId: ID }],
    ];

    for (const [source, body] of cases) {
      it(`sends a ${source.kind} with its own ids and no others (${JSON.stringify(body)})`, () => {
        // Exact equality: the server refuses a reference that carries another kind's field, null included.
        expect(encodeCreativeContextSource(source)).toEqual(body);
      });
    }
  });

  describe('encodeCreativeContextDraft', () => {
    it('sends nothing for what the creator has not said', () => {
      expect(encodeCreativeContextDraft({})).toEqual({});
      expect(encodeCreativeContextDraft({ workingTitle: null, channelKeys: [], day: null })).toEqual({});
    });

    it('sends the words, channels, day, theme and source it was given', () => {
      expect(
        encodeCreativeContextDraft({
          workingTitle: 'Soda bread',
          pictureBrief: 'Overhead.',
          channelKeys: ['blog'],
          day: 'Monday',
          weeklyThemeKey: 'meat-free-monday',
          from: { kind: 'GeneratedImage', generatedImageId: ID },
        }),
      ).toEqual({
        workingTitle: 'Soda bread',
        pictureBrief: 'Overhead.',
        channelKeys: ['blog'],
        day: 'Monday',
        weeklyThemeKey: 'meat-free-monday',
        from: { kind: 'GeneratedImage', generatedImageId: ID },
      });
    });
  });

  describe('encodeCreativeContextPatch', () => {
    it('always carries the token and otherwise only what was set', () => {
      expect(encodeCreativeContextPatch({ workingTitle: 'New' }, 'tok')).toEqual({
        expectedConcurrencyToken: 'tok',
        workingTitle: 'New',
      });
    });

    it('sends null for a field being cleared, because absent and null are different instructions', () => {
      expect(encodeCreativeContextPatch({ day: null, weeklyThemeKey: null, channelKeys: null }, 'tok')).toEqual({
        expectedConcurrencyToken: 'tok',
        day: null,
        weeklyThemeKey: null,
        channelKeys: null,
      });
    });

    it('leaves out a property that is present but undefined', () => {
      expect(encodeCreativeContextPatch({ workingTitle: undefined, archived: true }, 'tok')).toEqual({
        expectedConcurrencyToken: 'tok',
        archived: true,
      });
    });
  });

  describe('creativeContextSourceKey', () => {
    it('is the same for the same source and different for a different one', () => {
      const a = creativeContextSourceKey({ kind: 'Recipe', recipeId: ID });

      expect(creativeContextSourceKey({ kind: 'Recipe', recipeId: ID })).toBe(a);
      expect(creativeContextSourceKey({ kind: 'Recipe', recipeId: ID, recipeVersionId: null })).toBe(a);
      expect(creativeContextSourceKey({ kind: 'Recipe', recipeId: OTHER })).not.toBe(a);
      expect(creativeContextSourceKey({ kind: 'Recipe', recipeId: ID, recipeVersionId: OTHER })).not.toBe(a);
      expect(creativeContextSourceKey({ kind: 'PromptRecord', promptRecordId: ID })).not.toBe(a);
    });
  });
});
