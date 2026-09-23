import { TestBed } from '@angular/core/testing';

import { RecipeLibraryFixtureService } from './recipe-library-fixture.service';

describe('RecipeLibraryFixtureService', () => {
  it('resolves a non-empty list of recipes with distinct ids', async () => {
    const service = TestBed.inject(RecipeLibraryFixtureService);
    const recipes = await service.load();

    expect(recipes.length).toBeGreaterThan(0);
    expect(new Set(recipes.map((recipe) => recipe.id)).size).toBe(recipes.length);
  });
});
