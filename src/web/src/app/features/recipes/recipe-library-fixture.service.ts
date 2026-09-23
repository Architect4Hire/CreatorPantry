import { Injectable } from '@angular/core';

import { RecipeStatus } from '../../models/recipe.models';

export interface RecipeLibraryItem {
  readonly id: string;
  readonly title: string;
  readonly status: RecipeStatus;
  readonly updatedAt: string;
}

const FIXTURE_RECIPES: readonly RecipeLibraryItem[] = [
  { id: '11111111-1111-4111-8111-111111111111', title: 'Weeknight Chili', status: 'Ready', updatedAt: '2026-09-18T16:04:00Z' },
  { id: '22222222-2222-4222-8222-222222222222', title: 'Sheet-Pan Gnocchi', status: 'Draft', updatedAt: '2026-09-20T09:30:00Z' },
  { id: '33333333-3333-4333-8333-333333333333', title: 'Grandma\'s Apple Pie', status: 'Archived', updatedAt: '2026-06-02T12:00:00Z' },
];

/**
 * Stands in for the Phase 6 recipe search endpoint: a real Promise-based load so the library shell's
 * loading/error/retry states are genuine and testable, without HttpClient or a backend dependency.
 * Phase 6 replaces this service with one backed by the real paged search API — the component only
 * depends on `load()`'s shape, not on this fixture data.
 */
@Injectable({ providedIn: 'root' })
export class RecipeLibraryFixtureService {
  async load(): Promise<readonly RecipeLibraryItem[]> {
    return FIXTURE_RECIPES;
  }
}
