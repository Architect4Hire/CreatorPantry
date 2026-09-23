import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpCardComponent, CpEmptyStateComponent, CpListShellComponent, CpListShellState, CpStatusPillComponent, CpStatusPillTone } from '@creator-pantry/ui';

import { RecipeStatus } from '../../models/recipe.models';
import { RecipeLibraryFixtureService, RecipeLibraryItem } from './recipe-library-fixture.service';

type LibraryState =
  | { readonly status: 'loading' }
  | { readonly status: 'error' }
  | { readonly status: 'ready'; readonly recipes: readonly RecipeLibraryItem[] };

const STATUS_TONE: Record<RecipeStatus, CpStatusPillTone> = { Draft: 'neutral', Ready: 'success', Archived: 'stale' };

/**
 * The recipe-library route shell (`:workspaceSlug/recipes`). Recipes come from RecipeLibraryFixtureService,
 * a local stand-in for Phase 6's search endpoint — this component never injects HttpClient and knows
 * nothing about the fixture's contents, only its loading/error contract, so swapping in the real
 * search-backed service later is a provider change, not a rewrite here.
 */
@Component({
  selector: 'cp-recipe-library',
  standalone: true,
  imports: [RouterLink, DatePipe, CpButtonComponent, CpCardComponent, CpEmptyStateComponent, CpListShellComponent, CpStatusPillComponent],
  templateUrl: './recipe-library.component.html',
  styleUrl: './recipe-library.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeLibraryComponent {
  private readonly fixture = inject(RecipeLibraryFixtureService);

  private readonly stateSignal = signal<LibraryState>({ status: 'loading' });
  readonly state = this.stateSignal.asReadonly();

  constructor() {
    void this.load();
  }

  retry(): void {
    void this.load();
  }

  listShellState(): CpListShellState {
    const state = this.stateSignal();
    if (state.status === 'ready') return state.recipes.length === 0 ? 'empty' : 'ready';
    return state.status;
  }

  statusTone(status: RecipeStatus): CpStatusPillTone {
    return STATUS_TONE[status];
  }

  readyRecipes(): readonly RecipeLibraryItem[] {
    const state = this.stateSignal();
    return state.status === 'ready' ? state.recipes : [];
  }

  private async load(): Promise<void> {
    this.stateSignal.set({ status: 'loading' });
    try {
      const recipes = await this.fixture.load();
      this.stateSignal.set({ status: 'ready', recipes });
    } catch {
      this.stateSignal.set({ status: 'error' });
    }
  }
}
