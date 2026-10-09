import {
  CONTENT_SEED_TOKEN_MAX_LENGTH,
  contentSeedFieldError,
  contentSeedQueryParams,
  decodeContentSeed,
  isContentSeedToken,
} from './content-seed.models';

function facet(key: string, displayName: string, pinned = false): Record<string, unknown> {
  return { key, displayName, pinned };
}

function seedPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    token: 'AbC_123-x',
    cuisine: facet('thai', 'Thai'),
    dishType: facet('main-course', 'Main course'),
    method: { ...facet('stir-fry', 'Stir-fry'), requiresSafetyCaution: false },
    photographyStyle: facet('overhead-flat-lay', 'Overhead flat lay'),
    channel: facet('instagram', 'Instagram', true),
    day: { day: 'Wednesday', pinned: true, theme: facet('midweek-meals', 'Midweek meals') },
    occasion: facet('weeknight', 'Weeknight'),
    description: 'Develop a Thai main course using the stir-fry method.',
    ...overrides,
  };
}

describe('content-seed.models', () => {
  describe('decodeContentSeed', () => {
    it('reads a complete seed, including the pinned flag and the safety flag', () => {
      const seed = decodeContentSeed(seedPayload());

      expect(seed).not.toBeNull();
      expect(seed?.token).toBe('AbC_123-x');
      expect(seed?.cuisine?.displayName).toBe('Thai');
      expect(seed?.channel?.pinned).toBeTrue();
      expect(seed?.method?.requiresSafetyCaution).toBeFalse();
      expect(seed?.day.day).toBe('Wednesday');
      expect(seed?.day.theme?.displayName).toBe('Midweek meals');
    });

    it('reads a seed whose catalogues gave nothing, because an absent facet is not an error', () => {
      const seed = decodeContentSeed(
        seedPayload({
          cuisine: null,
          dishType: null,
          method: null,
          photographyStyle: null,
          channel: null,
          occasion: null,
          day: { day: 'Sunday', pinned: false, theme: null },
        }),
      );

      expect(seed).not.toBeNull();
      expect(seed?.cuisine).toBeNull();
      expect(seed?.method).toBeNull();
      expect(seed?.day.theme).toBeNull();
    });

    it('reads Sunday, the day whose enum value is zero', () => {
      const seed = decodeContentSeed(seedPayload({ day: { day: 'Sunday', pinned: false, theme: null } }));

      expect(seed?.day.day).toBe('Sunday');
    });

    it('refuses a method with no safety flag rather than assuming one', () => {
      const seed = decodeContentSeed(seedPayload({ method: facet('pressure-canning', 'Pressure canning') }));

      expect(seed).toBeNull();
    });

    it('refuses a day that is not a day of the week', () => {
      expect(decodeContentSeed(seedPayload({ day: { day: 'Someday', pinned: false, theme: null } }))).toBeNull();
    });

    it('refuses a malformed facet, which is not the same as an absent one', () => {
      expect(decodeContentSeed(seedPayload({ cuisine: { key: 'thai' } }))).toBeNull();
    });

    it('refuses a seed with no token, since a seed that cannot be reproduced is not one', () => {
      expect(decodeContentSeed(seedPayload({ token: '' }))).toBeNull();
      expect(decodeContentSeed(seedPayload({ token: 7 }))).toBeNull();
    });

    it('reads which parts are the linked recipe\'s own, and the recipe the idea was built around', () => {
      const seed = decodeContentSeed(
        seedPayload({
          cuisine: { ...facet('thai', 'Thai'), fromRecipe: true },
          recipe: { recipeId: 'recipe-1', recipeVersionId: null, title: 'Green Curry' },
        }),
      );

      expect(seed?.cuisine?.fromRecipe).toBeTrue();
      expect(seed?.dishType?.fromRecipe).toBeFalse();
      expect(seed?.recipe).toEqual({ recipeId: 'recipe-1', recipeVersionId: null, title: 'Green Curry' });
    });

    it('reads an idea kept before seeds knew about recipes as one built around none', () => {
      // A picked idea is stored whole on the device, so an older one has neither field.
      const seed = decodeContentSeed(seedPayload());

      expect(seed?.recipe).toBeNull();
      expect(seed?.cuisine?.fromRecipe).toBeFalse();
      expect(seed?.method?.fromRecipe).toBeFalse();
    });

    it('refuses a malformed recipe, which is not the same as none', () => {
      expect(decodeContentSeed(seedPayload({ recipe: { title: 'Green Curry' } }))).toBeNull();
      expect(decodeContentSeed(seedPayload({ recipe: 'recipe-1' }))).toBeNull();
      expect(decodeContentSeed(seedPayload({ cuisine: { ...facet('thai', 'Thai'), fromRecipe: 'yes' } }))).toBeNull();
    });

    it('refuses anything that is not an object', () => {
      expect(decodeContentSeed(null)).toBeNull();
      expect(decodeContentSeed('a seed')).toBeNull();
    });
  });

  describe('contentSeedQueryParams', () => {
    it('names every parameter the way the server binds it', () => {
      const params = contentSeedQueryParams({
        token: 'spring-bakes',
        cuisine: 'thai',
        dishType: 'main-course',
        method: 'stir-fry',
        photographyStyle: 'overhead-flat-lay',
        channel: 'instagram',
        occasion: 'weeknight',
        day: 'Friday',
      });

      // PascalCase, because ContentSeedQueryViewModel binds by property name: camelCase keys would bind to
      // nothing and every pin would be silently ignored.
      expect(params).toEqual({
        Token: 'spring-bakes',
        Cuisine: 'thai',
        DishType: 'main-course',
        Method: 'stir-fry',
        PhotographyStyle: 'overhead-flat-lay',
        Channel: 'instagram',
        Occasion: 'weeknight',
        Day: 'Friday',
      });
    });

    it('names the linked recipe and its pinned version', () => {
      expect(contentSeedQueryParams({ recipeId: 'recipe-1', recipeVersionId: 'version-1' })).toEqual({
        RecipeId: 'recipe-1',
        RecipeVersionId: 'version-1',
      });
      expect(contentSeedQueryParams({ recipeId: 'recipe-1', recipeVersionId: null })).toEqual({ RecipeId: 'recipe-1' });
    });

    it('never sends a version without its recipe, which the server refuses', () => {
      expect(contentSeedQueryParams({ recipeId: null, recipeVersionId: 'version-1' })).toEqual({});
    });

    it('leaves out what was not asked for, rather than sending it empty', () => {
      expect(contentSeedQueryParams({})).toEqual({});
      expect(contentSeedQueryParams({ token: null, cuisine: '  ', day: null })).toEqual({});
    });

    it('trims a pasted key', () => {
      expect(contentSeedQueryParams({ token: '  spring-bakes  ' })).toEqual({ Token: 'spring-bakes' });
    });
  });

  describe('contentSeedFieldError', () => {
    it('finds a field under either casing of its first letter', () => {
      expect(contentSeedFieldError({ Cuisine: ['No.'] }, 'Cuisine')).toBe('No.');
      expect(contentSeedFieldError({ cuisine: ['No.'] }, 'Cuisine')).toBe('No.');
      expect(contentSeedFieldError({ recipeId: ['Gone.'] }, 'RecipeId')).toBe('Gone.');
      expect(contentSeedFieldError({}, 'Cuisine')).toBe('');
    });
  });

  describe('isContentSeedToken', () => {
    it('accepts a code a creator could invent', () => {
      expect(isContentSeedToken('spring-bakes')).toBeTrue();
      expect(isContentSeedToken('Ab_9-x')).toBeTrue();
    });

    it('refuses anything that would not survive being pasted into a link', () => {
      expect(isContentSeedToken('')).toBeFalse();
      expect(isContentSeedToken('spring bakes')).toBeFalse();
      expect(isContentSeedToken('spring/bakes')).toBeFalse();
      expect(isContentSeedToken('a'.repeat(CONTENT_SEED_TOKEN_MAX_LENGTH + 1))).toBeFalse();
    });

    it('accepts a code exactly at the limit', () => {
      expect(isContentSeedToken('a'.repeat(CONTENT_SEED_TOKEN_MAX_LENGTH))).toBeTrue();
    });
  });
});
