import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { RecipeVersionComparison, RecipeVersionHistoryEntry } from '../../../models/recipe-version.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeVersionComparisonComponent } from '../../recipes/recipe-version-comparison.component';
import { linkedRecipeId } from '../recipe-steps/recipe-run';

/** How many pages of history are read looking for the version the run began on. */
const MAX_HISTORY_PAGES = 5;

type Compared =
  | { readonly status: 'loading' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'unchanged' }
  | { readonly status: 'missing' }
  | { readonly status: 'ready'; readonly from: number; readonly to: number; readonly comparison: RecipeVersionComparison };

/**
 * "Compare with before" (13A.15): the recipe as it stood when this run began, beside how it is now — the recipe
 * page's own comparison, worked out by the server, and read only. Optional, and done on the creator's word.
 */
@Component({
  selector: 'cp-recipe-improve-compare-step',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, RecipeVersionComparisonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (compared().status) {
      @case ('loading') {
        <p class="cp-muted" role="status">Comparing your recipe with how it was…</p>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          The versions couldn’t be compared just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('unchanged') {
        <p class="plain">Nothing has changed since you started.</p>
      }
      @case ('missing') {
        <cp-notice tone="warning" role="status">There’s no recipe chosen yet, so nothing to compare. Go back to the first step and choose one.</cp-notice>
      }
      @case ('ready') {
        @if (ready(); as shown) {
          <p class="plain">Version {{ shown.from }}, from before you started, beside version {{ shown.to }}, as it is now. Every version is kept.</p>
          <cp-recipe-version-comparison [comparison]="shown.comparison" />
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .plain {
      margin: 0;
    }
  `,
})
export class RecipeImproveCompareStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly recipes = inject(RecipeService);

  protected readonly compared = signal<Compared>({ status: 'loading' });

  protected ready() {
    const compared = this.compared();
    return compared.status === 'ready' ? compared : null;
  }

  constructor() {
    const unregister = inject(WorkflowStepPort).register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    const id = linkedRecipeId(this.run());
    if (id === null) {
      this.compared.set({ status: 'missing' });
      return;
    }

    this.compared.set({ status: 'loading' });
    const versions = await this.history(id);
    if (versions === null) {
      this.compared.set({ status: 'unavailable' });
      return;
    }

    // The version current when the run began is the newest one made before it; now is the newest of all. A recipe
    // whose every version came after the run began — chosen after it was made elsewhere — is compared from its
    // first, as is one whose start lies further back than the history read: the oldest version read stands in.
    const began = new Date(this.run().startedAt).getTime();
    const before = versions.filter((version) => new Date(version.createdAt).getTime() <= began);
    const numbers = versions.map((version) => version.versionNumber);
    const from = before.length > 0
      ? Math.max(...before.map((version) => version.versionNumber))
      : numbers.length > 0 ? Math.min(...numbers) : null;
    const to = numbers.length > 0 ? Math.max(...numbers) : null;

    if (from === null || to === null || from === to) {
      this.compared.set({ status: 'unchanged' });
      return;
    }

    const outcome = await this.recipes.compareVersions(this.workspaceSlug(), id, from, to);
    this.compared.set(outcome.status === 'found' ? { status: 'ready', from, to, comparison: outcome.comparison } : { status: 'unavailable' });
  }

  /** The recipe's versions, as far back as the run began, or null when they could not be read. */
  private async history(recipeId: string): Promise<RecipeVersionHistoryEntry[] | null> {
    const versions: RecipeVersionHistoryEntry[] = [];
    const began = new Date(this.run().startedAt).getTime();
    let cursor: string | undefined;

    for (let page = 0; page < MAX_HISTORY_PAGES; page++) {
      const outcome = await this.recipes.getVersionHistory(this.workspaceSlug(), recipeId, cursor);
      if (outcome.status !== 'found') return null;
      versions.push(...outcome.page.items);

      const reachedBefore = outcome.page.items.some((version) => new Date(version.createdAt).getTime() <= began);
      if (reachedBefore || outcome.page.nextCursor === null) break;
      cursor = outcome.page.nextCursor;
    }

    return versions;
  }
}
