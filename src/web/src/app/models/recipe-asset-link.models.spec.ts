import {
  LinkRecipeAssetRequest,
  RECIPE_ASSET_ROLES,
  RECIPE_ASSET_ROLE_HEADINGS,
  RECIPE_ASSET_ROLE_HINTS,
  RECIPE_ASSET_ROLE_LABELS,
  encodeLinkRecipeAssetRequest,
  groupRecipeAssetLinks,
  recipeAssetPinText,
  recipeMediaStepLabel,
} from './recipe-asset-link.models';
import { RecipeAssetLink } from './recipe.models';

function request(overrides: Partial<LinkRecipeAssetRequest> = {}): LinkRecipeAssetRequest {
  return {
    mediaAssetId: 'a1',
    role: 'Gallery',
    instructionStepId: null,
    versionNumber: null,
    caption: null,
    expectedConcurrencyToken: 'token-1',
    ...overrides,
  };
}

function link(overrides: Partial<RecipeAssetLink>): RecipeAssetLink {
  return {
    id: 'l1',
    sortOrder: 0,
    mediaAssetId: 'a1',
    mediaAssetVersionNumber: null,
    instructionStepId: null,
    role: 'Gallery',
    caption: null,
    ...overrides,
  };
}

describe('recipe asset-link models', () => {
  describe('encodeLinkRecipeAssetRequest', () => {
    it('sends only the asset, the role and the token when nothing else applies', () => {
      expect(encodeLinkRecipeAssetRequest(request())).toEqual({
        mediaAssetId: 'a1',
        role: 'Gallery',
        expectedConcurrencyToken: 'token-1',
      });
    });

    it('sends the step for a step picture, the kept version, and the trimmed caption', () => {
      expect(
        encodeLinkRecipeAssetRequest(
          request({ role: 'Step', instructionStepId: 's2', versionNumber: 3, caption: '  Folding in.  ' }),
        ),
      ).toEqual({
        mediaAssetId: 'a1',
        role: 'Step',
        instructionStepId: 's2',
        versionNumber: 3,
        caption: 'Folding in.',
        expectedConcurrencyToken: 'token-1',
      });
    });

    it('never sends a step with a role that cannot have one, whatever the caller still held', () => {
      const body = encodeLinkRecipeAssetRequest(request({ role: 'Hero', instructionStepId: 's2' }));

      expect('instructionStepId' in body).toBeFalse();
    });

    it('leaves out a caption that is only white space', () => {
      expect('caption' in encodeLinkRecipeAssetRequest(request({ caption: '   ' }))).toBeFalse();
    });

    it('never sends a workspace', () => {
      expect(Object.keys(encodeLinkRecipeAssetRequest(request())).some((key) => /workspace/i.test(key))).toBeFalse();
    });
  });

  describe('role vocabulary', () => {
    it('has a label, a hint and a heading for every role the server knows', () => {
      for (const role of RECIPE_ASSET_ROLES) {
        expect(RECIPE_ASSET_ROLE_LABELS[role]).withContext(role).toBeTruthy();
        expect(RECIPE_ASSET_ROLE_HINTS[role]).withContext(role).toBeTruthy();
        expect(RECIPE_ASSET_ROLE_HEADINGS[role]).withContext(role).toBeTruthy();
      }

      expect([...RECIPE_ASSET_ROLES].sort()).toEqual(['Gallery', 'Hero', 'Process', 'Social', 'Step']);
    });
  });

  describe('groupRecipeAssetLinks', () => {
    it('groups by role in the order roles are offered, each group in the recipe’s own order', () => {
      const groups = groupRecipeAssetLinks([
        link({ id: 'social', role: 'Social', sortOrder: 0 }),
        link({ id: 'step-b', role: 'Step', sortOrder: 5 }),
        link({ id: 'hero', role: 'Hero', sortOrder: 3 }),
        link({ id: 'step-a', role: 'Step', sortOrder: 1 }),
      ]);

      expect(groups.map((group) => group.heading)).toEqual(['Lead picture', 'Step pictures', 'Social']);
      expect(groups[1].links.map((each) => each.id)).toEqual(['step-a', 'step-b']);
    });

    it('has no group for a role with no pictures', () => {
      expect(groupRecipeAssetLinks([])).toEqual([]);
    });
  });

  describe('recipeAssetPinText', () => {
    it('says a link follows the current version, and which that is when known', () => {
      expect(recipeAssetPinText(null, 3)).toBe('Follows the current version (version 3)');
      expect(recipeAssetPinText(null, null)).toBe('Follows the current version');
    });

    it('says which version is kept, and that a newer one exists only when one does', () => {
      expect(recipeAssetPinText(1, 3)).toBe('Kept at version 1 · version 3 is newer');
      expect(recipeAssetPinText(3, 3)).toBe('Kept at version 3');
    });

    it('claims nothing about a newer version when the asset could not be read', () => {
      expect(recipeAssetPinText(1, null)).toBe('Kept at version 1');
    });
  });

  describe('recipeMediaStepLabel', () => {
    it('names a step by its number and the creator’s own words', () => {
      expect(recipeMediaStepLabel({ id: 's2', number: 2, text: '  Fold in\nthe flour. ' })).toBe('Step 2: Fold in the flour.');
    });

    it('shortens a long step and falls back to the number for an empty one', () => {
      const long = recipeMediaStepLabel({ id: 's1', number: 1, text: 'word '.repeat(40) });

      expect(long.endsWith('…')).toBeTrue();
      expect(long.length).toBeLessThan(75);
      expect(recipeMediaStepLabel({ id: 's3', number: 3, text: '   ' })).toBe('Step 3');
    });
  });
});
