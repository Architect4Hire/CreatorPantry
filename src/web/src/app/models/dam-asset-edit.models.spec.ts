import {
  DAM_ASSET_LIMITS,
  DamAssetDetail,
  DamAssetMetadataDraft,
  damMetadataChanges,
  damMetadataDraftOf,
  damMetadataFieldFor,
  encodeDamAssetPatch,
  rebaseDamMetadataDraft,
  validateDamMetadataDraft,
} from './dam-asset.models';
import { decodeWorkspaceTags } from './workspace-tag.models';

function draft(overrides: Partial<DamAssetMetadataDraft> = {}): DamAssetMetadataDraft {
  return {
    title: 'Soda bread hero',
    description: 'Overhead.',
    altText: 'A round loaf on linen.',
    channelKey: 'instagram',
    platformKey: 'reels',
    day: 'Wednesday',
    styleKey: 'overhead-linen',
    rightsHolder: 'Sam Baker',
    attributionText: 'Photo: Sam Baker',
    tagIds: ['t1', 't2'],
    ...overrides,
  };
}

function asset(): DamAssetDetail {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    description: null,
    altText: null,
    kind: 'Original',
    channelKey: null,
    platformKey: 'reels',
    day: null,
    styleKey: null,
    rightsHolder: null,
    attributionText: null,
    tags: [{ id: 't1', name: 'Weeknight' }],
    currentVersion: null,
    versions: [],
    utilizationCount: 0,
    recipeLinks: [],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [],
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    concurrencyToken: 'token-1',
  };
}

describe('DAM asset edit models', () => {
  describe('damMetadataDraftOf', () => {
    it('starts the form from the asset, with empty boxes for what was never filled in', () => {
      expect(damMetadataDraftOf(asset())).toEqual({
        title: 'Soda bread hero',
        description: '',
        altText: '',
        channelKey: '',
        platformKey: 'reels',
        day: null,
        styleKey: '',
        rightsHolder: '',
        attributionText: '',
        tagIds: ['t1'],
      });
    });
  });

  describe('damMetadataChanges', () => {
    it('finds nothing changed in an untouched draft', () => {
      expect(damMetadataChanges(draft(), draft())).toEqual([]);
    });

    it('does not count surrounding whitespace, or the order of tags, as a change', () => {
      expect(damMetadataChanges(draft(), draft({ title: '  Soda bread hero ', tagIds: ['t2', 't1'] }))).toEqual([]);
    });

    it('names each field that changed', () => {
      expect(
        damMetadataChanges(draft(), draft({ title: 'New', altText: '', day: null, tagIds: ['t1'] })),
      ).toEqual(['title', 'altText', 'day', 'tagIds']);
    });
  });

  describe('encodeDamAssetPatch', () => {
    it('sends the token and nothing else when nothing changed', () => {
      expect(encodeDamAssetPatch('token-1', draft(), draft())).toEqual({ expectedConcurrencyToken: 'token-1' });
    });

    it('sends only the fields that changed, so an untouched field can never be blanked', () => {
      const body = encodeDamAssetPatch('token-1', draft(), draft({ title: ' Better title ' }));

      expect(body).toEqual({ expectedConcurrencyToken: 'token-1', title: 'Better title' });
      expect(Object.keys(body)).not.toContain('attributionText');
      expect(Object.keys(body)).not.toContain('altText');
    });

    it('sends an emptied field as null, which is how the route is told to clear it', () => {
      const body = encodeDamAssetPatch('token-1', draft(), draft({ altText: '   ', rightsHolder: '', channelKey: '' }));

      expect(body).toEqual({ expectedConcurrencyToken: 'token-1', altText: null, channelKey: null, rightsHolder: null });
    });

    it('clears the day with null and sets it by name', () => {
      expect(encodeDamAssetPatch('t', draft(), draft({ day: null }))['day']).toBeNull();
      expect(encodeDamAssetPatch('t', draft(), draft({ day: 'Sunday' }))['day']).toBe('Sunday');
    });

    it("publishes the tag set under the route's own name, whole, and as an empty list when all are removed", () => {
      expect(encodeDamAssetPatch('t', draft(), draft({ tagIds: ['t1', 't3'] }))['tags']).toEqual(['t1', 't3']);
      expect(encodeDamAssetPatch('t', draft(), draft({ tagIds: [] }))['tags']).toEqual([]);
      expect(Object.keys(encodeDamAssetPatch('t', draft(), draft({ tagIds: [] })))).not.toContain('tagIds');
    });

    it('never carries a workspace, an owner, a kind or anything about the bytes', () => {
      const body = encodeDamAssetPatch('t', draft(), draft({ title: 'x', description: 'y', tagIds: [] }));

      for (const banned of ['workspaceId', 'kind', 'mediaType', 'versionNumber', 'objectKey', 'cuisineId', 'courseId']) {
        expect(Object.keys(body)).not.toContain(banned);
      }
    });
  });

  describe('validateDamMetadataDraft', () => {
    it('accepts a draft with only a title', () => {
      expect(
        validateDamMetadataDraft(
          draft({ description: '', altText: '', channelKey: '', platformKey: '', styleKey: '', rightsHolder: '', attributionText: '', day: null, tagIds: [] }),
        ),
      ).toEqual({});
    });

    it('refuses an empty title, which is the one field that cannot be cleared', () => {
      expect(validateDamMetadataDraft(draft({ title: '   ' })).title).toContain('needs a title');
    });

    it("refuses text past each of the server's limits, and accepts text exactly at them", () => {
      const at = (length: number): string => 'x'.repeat(length);

      expect(validateDamMetadataDraft(draft({ title: at(DAM_ASSET_LIMITS.titleMaxLength) }))).toEqual({});
      expect(validateDamMetadataDraft(draft({ title: at(DAM_ASSET_LIMITS.titleMaxLength + 1) })).title).toContain('200');
      expect(validateDamMetadataDraft(draft({ description: at(2001) })).description).toContain('2000');
      expect(validateDamMetadataDraft(draft({ altText: at(1001) })).altText).toContain('1000');
      expect(validateDamMetadataDraft(draft({ platformKey: at(65) })).platformKey).toContain('64');
      expect(validateDamMetadataDraft(draft({ styleKey: at(65) })).styleKey).toContain('64');
      expect(validateDamMetadataDraft(draft({ rightsHolder: at(501) })).rightsHolder).toContain('500');
      expect(validateDamMetadataDraft(draft({ attributionText: at(501) })).attributionText).toContain('500');
    });
  });

  describe('rebaseDamMetadataDraft', () => {
    it("keeps the creator's own changes and takes the latest for everything they did not touch", () => {
      const stale = draft();
      const mine = draft({ title: 'My title', tagIds: ['t1'] });
      const latest = draft({ title: 'Their title', description: 'Their description', tagIds: ['t1', 't2', 't9'] });

      const rebased = rebaseDamMetadataDraft(stale, mine, latest);

      expect(rebased.title).toBe('My title');
      expect(rebased.tagIds).toEqual(['t1']);
      expect(rebased.description).toBe('Their description');
    });

    it('leaves nothing to save when the latest already has everything the creator changed', () => {
      const stale = draft();
      const mine = draft({ title: 'Same title' });
      const latest = draft({ title: 'Same title' });

      expect(damMetadataChanges(latest, rebaseDamMetadataDraft(stale, mine, latest))).toEqual([]);
    });

    it("never sends a stale copy of an untouched field back over someone else's edit", () => {
      const stale = draft({ attributionText: 'Old credit' });
      const mine = draft({ attributionText: 'Old credit', title: 'My title' });
      const latest = draft({ attributionText: 'New credit' });

      const body = encodeDamAssetPatch('t2', latest, rebaseDamMetadataDraft(stale, mine, latest));

      expect(body).toEqual({ expectedConcurrencyToken: 't2', title: 'My title' });
    });
  });

  describe('damMetadataFieldFor', () => {
    it("maps the server's field names onto the form's, whatever their case", () => {
      expect(damMetadataFieldFor('title')).toBe('title');
      expect(damMetadataFieldFor('AltText')).toBe('altText');
      expect(damMetadataFieldFor('tags')).toBe('tagIds');
      expect(damMetadataFieldFor('WorkspaceTagIds')).toBe('tagIds');
      expect(damMetadataFieldFor('expectedConcurrencyToken')).toBeNull();
    });
  });

  describe('decodeWorkspaceTags', () => {
    it('reads the list the route sends, and an empty one', () => {
      expect(decodeWorkspaceTags([{ id: 't1', name: 'Weeknight' }])).toEqual([{ id: 't1', name: 'Weeknight' }]);
      expect(decodeWorkspaceTags([])).toEqual([]);
    });

    it('carries nothing but the id and the name', () => {
      const tags = decodeWorkspaceTags([{ id: 't1', name: 'Weeknight', workspaceId: 'w1', normalizedName: 'weeknight' }]);

      expect(Object.keys(tags?.[0] ?? {})).toEqual(['id', 'name']);
    });

    it('fails the whole list on one unreadable entry, and refuses a body that is not a list', () => {
      expect(decodeWorkspaceTags([{ id: 't1', name: 'Weeknight' }, { id: '' }])).toBeNull();
      expect(decodeWorkspaceTags({ items: [] })).toBeNull();
    });
  });
});
