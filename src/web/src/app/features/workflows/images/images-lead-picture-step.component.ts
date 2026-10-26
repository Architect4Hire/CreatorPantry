import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { RecipeMediaStep } from '../../../models/recipe-asset-link.models';
import { RecipeDetail } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { CreativeContextService } from '../../../services/creative-context.service';
import { DamAssetService } from '../../../services/dam-asset.service';
import { RecipeAssetLinkService } from '../../../services/recipe-asset-link.service';
import { RecipeService } from '../../../services/recipe.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeMediaChanged, RecipeMediaComponent } from '../../recipes/recipe-media.component';
import { newestLinked, runContextId } from '../creative-work/creative-work-run';

type Read =
  | { readonly status: 'loading' }
  | { readonly status: 'gone' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly recipe: RecipeDetail };

/** A picture this run saved to the library, by its own title. */
interface Kept {
  readonly id: string;
  readonly title: string;
}

/** What a press of "Use … as the lead picture" can end in, said in a sentence. Null while nothing is wrong. */
const LINK_PROBLEMS: Readonly<Record<string, string>> = {
  hero_exists: 'Your recipe already has a lead picture. Read it again below.',
  conflict: 'Your recipe changed since this page read it. It has been read again — try once more.',
  archived_conflict: 'This recipe is archived, so its pictures can’t be changed.',
  target_refused: 'That picture can’t be used any more. It may have been removed from your library.',
  forbidden: 'You don’t have permission to change this recipe’s pictures.',
  not_found: 'This recipe can’t be found any more.',
};

/**
 * "Use it as your recipe's lead picture", the last step of "Create food images" (13A.19, RCPUB-005).
 *
 * <p><strong>Orchestration only.</strong> Linking a picture to the recipe is the recipe's own media panel and its
 * own route, which write a version of the recipe like every other edit of it. What is this step's is one shortcut:
 * the pictures this run saved, each offered as the lead picture while the recipe has none — one press, the same
 * command the panel's dialog sends.</p>
 *
 * <p>The step is done when the recipe has a lead picture, which the server reads from the recipe itself.</p>
 */
@Component({
  selector: 'cp-images-lead-picture-step',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, RecipeMediaComponent, CpSkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{ read().status === 'loading' ? 'Reading your recipe…' : '' }}</p>
    @switch (read().status) {
      @case ('loading') {
        <!-- The recipe arriving, as a few lines. -->
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('gone') {
        <cp-notice tone="warning" role="status">
          The recipe this workflow is about can’t be found any more, so there is nothing to put a picture on. You can
          skip this step.
        </cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your recipe couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="reload()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (hasLeadPicture()) {
          <cp-notice tone="success" role="status">Your recipe has a lead picture.</cp-notice>
        } @else if (!canChange()) {
          <cp-notice role="note">You have view-only access, so you can’t change this recipe’s pictures.</cp-notice>
        } @else if (keptUnreadable()) {
          <cp-notice role="status">
            The pictures you saved in this workflow couldn’t be read just now, so they aren’t offered here. You can
            still choose one from your library below.
          </cp-notice>
        } @else if (offers().length > 0) {
          <p class="plain">Put one of the pictures you just saved at the top of “{{ title() }}”.</p>
          <div class="cp-actions">
            @for (picture of offers(); track picture.id; let first = $first) {
              <button
                cpButton
                type="button"
                [variant]="first ? 'primary' : 'secondary'"
                [attr.aria-disabled]="linking() || null"
                (click)="useAsLead(picture)"
              >
                Use “{{ picture.title }}” as the lead picture
              </button>
            }
          </div>
        }

        @if (problem(); as sentence) {
          <cp-notice tone="error" role="alert">{{ sentence }}</cp-notice>
        }

        @if (recipe(); as detail) {
          <cp-recipe-media
            [workspaceSlug]="workspaceSlug()"
            [recipeId]="detail.id"
            [links]="detail.assetLinks"
            [steps]="steps()"
            [concurrencyToken]="detail.concurrencyToken"
            [canChange]="canChange()"
            [isArchived]="detail.status === 'Archived'"
            (changed)="onMediaChanged($event)"
            (reloadRequested)="reload()"
          />
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
export class ImagesLeadPictureStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly recipes = inject(RecipeService);
  private readonly links = inject(RecipeAssetLinkService);
  private readonly contexts = inject(CreativeContextService);
  private readonly assets = inject(DamAssetService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly read = signal<Read>({ status: 'loading' });
  protected readonly kept = signal<readonly Kept[]>([]);
  /** True when the work this run saved its pictures on could not be read, so none could be offered. */
  protected readonly keptUnreadable = signal(false);
  protected readonly linking = signal(false);
  protected readonly problem = signal<string | null>(null);

  /** The key of the press being made, kept for its retry so a lost answer and a second press link once. */
  private linkKey: { readonly assetId: string; readonly key: string } | null = null;
  private gone = false;

  protected readonly recipe = computed(() => {
    const read = this.read();
    return read.status === 'ready' ? read.recipe : null;
  });

  protected readonly title = computed(() => this.recipe()?.title ?? '');

  protected readonly hasLeadPicture = computed(() => this.recipe()?.assetLinks.some((link) => link.role === 'Hero') ?? false);

  /** The pictures this run saved that the recipe does not already use. */
  protected readonly offers = computed(() => {
    const used = new Set(this.recipe()?.assetLinks.map((link) => link.mediaAssetId) ?? []);
    return this.kept().filter((picture) => !used.has(picture.id));
  });

  protected readonly canChange = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && role !== 'Viewer';
  });

  /** The recipe's saved steps, numbered through its groups, as the media panel offers a step picture. */
  protected readonly steps = computed<readonly RecipeMediaStep[]>(() => {
    const steps: RecipeMediaStep[] = [];
    let number = 0;
    for (const group of this.recipe()?.instructionGroups ?? []) {
      for (const step of group.steps) {
        number += 1;
        steps.push({ id: step.id, number, text: step.text });
      }
    }
    return steps;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    // Every change here is a command the server has already answered; there is nothing to keep before a move.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      saving: this.linking.asReadonly(),
      flush: () => Promise.resolve(true),
      ready: this.hasLeadPicture,
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.memberships.ensureLoaded();
    void this.reload();
    void this.readKept();
  }

  async reload(): Promise<void> {
    const recipeId = newestLinked(this.run(), 'Recipe');
    if (recipeId === null) {
      this.read.set({ status: 'gone' });
      return;
    }

    if (this.read().status !== 'ready') this.read.set({ status: 'loading' });
    const outcome = await this.recipes.getRecipeDetail(this.workspaceSlug(), recipeId);
    if (this.gone) return;

    this.read.set(
      outcome.status === 'found'
        ? { status: 'ready', recipe: outcome.recipe }
        : { status: outcome.status === 'not_found' ? 'gone' : 'unavailable' },
    );
  }

  protected async useAsLead(picture: Kept): Promise<void> {
    const recipe = this.recipe();
    if (recipe === null || this.linking()) return;

    if (this.linkKey?.assetId !== picture.id) this.linkKey = { assetId: picture.id, key: crypto.randomUUID() };
    this.linking.set(true);
    this.problem.set(null);

    const outcome = await this.links.link(
      this.workspaceSlug(),
      recipe.id,
      {
        mediaAssetId: picture.id,
        role: 'Hero',
        instructionStepId: null,
        versionNumber: null,
        caption: null,
        expectedConcurrencyToken: recipe.concurrencyToken,
      },
      this.linkKey.key,
    );
    if (this.gone) return;
    this.linking.set(false);

    if (outcome.status === 'linked') {
      this.linkKey = null;
      this.read.set({ status: 'ready', recipe: outcome.recipe });
      this.port.announce(`“${picture.title}” is now the lead picture of ${outcome.recipe.title}.`);
      return;
    }

    this.problem.set(LINK_PROBLEMS[outcome.status] ?? 'The picture couldn’t be linked just now. Trying again is safe.');
    // A recipe that moved, or that has a lead picture already, is read again so what is shown is what is true.
    if (outcome.status === 'conflict' || outcome.status === 'hero_exists') {
      this.linkKey = null;
      await this.reload();
    }
  }

  protected onMediaChanged(change: RecipeMediaChanged): void {
    this.read.set({ status: 'ready', recipe: change.recipe });
    this.problem.set(null);
    this.port.announce(change.message);
  }

  /** The pictures this run's work saved to the library, by title. One that cannot be read is not offered. */
  private async readKept(): Promise<void> {
    const contextId = runContextId(this.run());
    if (contextId === null) return;

    const slug = this.workspaceSlug();
    const outcome = await firstValueFrom(this.contexts.get(slug, contextId));
    if (this.gone) return;
    if (outcome.status !== 'found') {
      // A work that is gone has nothing to offer; one that could not be read is said, not passed over.
      this.keptUnreadable.set(outcome.status === 'unavailable');
      return;
    }

    const ids = outcome.context.references
      .filter((reference) => reference.kind === 'DamAsset' && reference.purpose === 'Keeper' && reference.mediaAssetId !== null)
      .map((reference) => reference.mediaAssetId!);

    const read = await Promise.all(
      [...new Set(ids)].map(async (id): Promise<Kept | null> => {
        const detail = await firstValueFrom(this.assets.detail(slug, id));
        return detail.status === 'found' ? { id, title: detail.asset.title } : null;
      }),
    );
    if (this.gone) return;

    this.kept.set(read.filter((picture): picture is Kept => picture !== null));
  }
}
