import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Title } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CpButtonComponent, CpCheckboxComponent, CpEmptyStateComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { APP_NAME } from '../../core/page-title.strategy';
import { PrintService } from '../../core/print.service';
import { ScreenWakeService } from '../../core/screen-wake.service';
import { RecipeDetail } from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';
import { ReferenceService } from '../../services/reference.service';

type LoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly recipe: RecipeDetail }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** One ingredient line as the cook reads it: the creator's own wording, and whether it may be left out. */
export interface KitchenIngredient {
  readonly id: string;
  readonly text: string;
  readonly isOptional: boolean;
}

export interface KitchenIngredientGroup {
  readonly id: string;
  readonly title: string | null;
  readonly ingredients: readonly KitchenIngredient[];
}

export interface KitchenStep {
  readonly id: string;
  /** Counted through the whole method, not per section: "step 7" has to mean one step. */
  readonly number: number;
  readonly text: string;
  /** The step's own time and temperature, already worded. Empty when it recorded neither. */
  readonly facts: readonly string[];
  readonly note: string | null;
}

export interface KitchenStepGroup {
  readonly id: string;
  readonly title: string | null;
  readonly steps: readonly KitchenStep[];
}

/** Words a count of minutes the way a cook says it: `45 min`, `1 hr`, `1 hr 20 min`. */
export function formatKitchenMinutes(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;

  if (hours === 0) return `${rest} min`;

  return rest === 0 ? `${hours} hr` : `${hours} hr ${rest} min`;
}

function bySortOrder<T extends { readonly sortOrder: number }>(items: readonly T[]): T[] {
  return [...items].sort((a, b) => a.sortOrder - b.sortOrder);
}

/**
 * The kitchen view: the saved recipe laid out to cook from, on a screen or on paper.
 *
 * The editor is where a recipe is written, and it is the wrong thing to cook from — a form of twelve tabs,
 * most of it about everything except what goes in the bowl next. This shows the two things a cook needs, the
 * ingredients and the method, in type large enough to read from across a counter, and prints as a clean
 * recipe page (see the `@media print` block in the stylesheet).
 *
 * **It reads and never writes.** Ticking a line off is this visit's bookkeeping, held in memory and gone on
 * leaving: it is not a fact about the recipe, so nothing about it is saved or sent.
 *
 * **It shows the saved recipe, in the creator's words.** An ingredient line is its `displayText` verbatim —
 * never reassembled from quantity and unit — and nothing here scales, converts or sums. Unsaved edits in the
 * editor are not here; the editor's own leave-guard is what says so on the way over.
 *
 * **It can keep the screen on.** A phone or tablet propped against the flour bag locks itself after a minute
 * of nobody touching it, which is the length of one step. The option is off until asked for, because a page
 * that quietly stopped a device from sleeping would be a surprise, and nothing about it is saved either.
 *
 * **A step's temperature needs a unit to mean anything.** The recipe carries the unit as an id, so the units
 * are read alongside it. When they cannot be, the temperatures are left out and the page says so, rather
 * than printing a bare `350` for a cook to guess the scale of.
 */
@Component({
  selector: 'cp-recipe-kitchen-view',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpCheckboxComponent, CpEmptyStateComponent, CpNoticeComponent],
  templateUrl: './recipe-kitchen-view.component.html',
  styleUrl: './recipe-kitchen-view.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeKitchenViewComponent {
  private readonly recipeService = inject(RecipeService);
  private readonly referenceService = inject(ReferenceService);
  private readonly printService = inject(PrintService);
  private readonly screenWake = inject(ScreenWakeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly titleService = inject(Title);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = this.resolveWorkspaceSlug();
  readonly recipeId = this.route.snapshot.paramMap.get('recipeId') ?? '';

  private readonly loadStateSignal = signal<LoadState>({ status: 'loading' });
  readonly loadState = this.loadStateSignal.asReadonly();

  /** Temperature unit abbreviations by id, or null when the units could not be read. */
  private readonly temperatureUnits = signal<ReadonlyMap<string, string> | null>(null);

  private readonly tickedIngredientIds = signal<ReadonlySet<string>>(new Set());

  constructor() {
    // Leaving the kitchen hands the screen back, whether by navigating away or by closing the tab.
    inject(DestroyRef).onDestroy(() => this.screenWake.release());

    void this.load();
  }

  // -------------------------------------------------------------------------
  // What is shown
  // -------------------------------------------------------------------------

  private readonly recipe = computed(() => {
    const state = this.loadState();
    return state.status === 'ready' ? state.recipe : null;
  });

  /** The yield, serving count and times, in the order a cook checks them before starting. */
  readonly facts = computed(() => {
    const recipe = this.recipe();
    if (recipe === null) return [];

    const facts: { readonly label: string; readonly value: string }[] = [];
    if (recipe.yieldText) facts.push({ label: 'Makes', value: recipe.yieldText });
    if (recipe.servingCount !== null) facts.push({ label: 'Serves', value: `${recipe.servingCount}` });
    if (recipe.prepTimeMinutes !== null) facts.push({ label: 'Prep', value: formatKitchenMinutes(recipe.prepTimeMinutes) });
    if (recipe.cookTimeMinutes !== null) facts.push({ label: 'Cook', value: formatKitchenMinutes(recipe.cookTimeMinutes) });
    if (recipe.restTimeMinutes !== null) facts.push({ label: 'Rest', value: formatKitchenMinutes(recipe.restTimeMinutes) });
    // The stored total, never a sum of the three above — the same rule the recipe itself keeps.
    if (recipe.totalTimeMinutes !== null) facts.push({ label: 'Total', value: formatKitchenMinutes(recipe.totalTimeMinutes) });

    return facts;
  });

  /** Groups with nothing in them are dropped: a heading over no lines is noise at a counter. */
  readonly ingredientGroups = computed<readonly KitchenIngredientGroup[]>(() =>
    bySortOrder(this.recipe()?.ingredientGroups ?? [])
      .map((group) => ({
        id: group.id,
        title: group.title,
        ingredients: bySortOrder(group.ingredients).map((line) => ({
          id: line.id,
          text: line.displayText,
          isOptional: line.isOptional,
        })),
      }))
      .filter((group) => group.ingredients.length > 0),
  );

  readonly stepGroups = computed<readonly KitchenStepGroup[]>(() => {
    const units = this.temperatureUnits();
    let number = 0;

    return bySortOrder(this.recipe()?.instructionGroups ?? [])
      .map((group) => ({
        id: group.id,
        title: group.title,
        steps: bySortOrder(group.steps).map((step) => {
          const facts: string[] = [];
          if (step.durationMinutes !== null) facts.push(formatKitchenMinutes(step.durationMinutes));

          if (step.temperatureValue !== null && units !== null) {
            const unit = step.temperatureUnitId === null ? undefined : units.get(step.temperatureUnitId);
            // A temperature whose unit is unknown is not shown as a number on its own; see the class note.
            if (unit !== undefined) facts.push(`${step.temperatureValue}${unit}`);
          }

          number += 1;

          return { id: step.id, number, text: step.text, facts, note: step.note };
        }),
      }))
      .filter((group) => group.steps.length > 0);
  });

  readonly equipment = computed(() => bySortOrder(this.recipe()?.equipment ?? []));

  readonly isEmpty = computed(() => this.ingredientGroups().length === 0 && this.stepGroups().length === 0);

  /**
   * Whether some step records a temperature this page is not showing.
   *
   * True when the units could not be read, and when a step names a unit the catalogue did not return. Either
   * way the cook is told, because a missing oven temperature is not something to find out mid-recipe.
   */
  readonly temperaturesMissing = computed(() => {
    const units = this.temperatureUnits();

    return (this.recipe()?.instructionGroups ?? []).some((group) =>
      group.steps.some(
        (step) =>
          step.temperatureValue !== null &&
          (units === null || step.temperatureUnitId === null || !units.has(step.temperatureUnitId)),
      ),
    );
  });

  // -------------------------------------------------------------------------
  // Ticking off
  // -------------------------------------------------------------------------
  //
  // Ingredients only. Steps had a box of their own and it earned nothing: the method is read in order, so
  // which step is next is where the cook's eye already is, and a box under every step put a control between
  // each one and the next.

  readonly anyTicked = computed(() => this.tickedIngredientIds().size > 0);

  isIngredientTicked(id: string): boolean {
    return this.tickedIngredientIds().has(id);
  }

  setIngredientTicked(id: string, ticked: boolean): void {
    this.tickedIngredientIds.update((ids) => withMembership(ids, id, ticked));
  }

  clearTicks(): void {
    this.tickedIngredientIds.set(new Set());
  }

  // -------------------------------------------------------------------------
  // Keeping the screen on
  // -------------------------------------------------------------------------

  /**
   * Whether to offer the option at all. A browser that cannot do it is offered nothing, because a control
   * that does nothing is worse than no control — the creator would prop the phone up and trust it.
   */
  readonly canKeepScreenAwake = this.screenWake.isSupported;

  private readonly keepScreenAwakeSignal = signal(false);

  /**
   * What the creator asked for, not whether a lock is held this instant: the browser drops the lock every
   * time the page is hidden and the service asks again on the way back, so a box following the lock itself
   * would untick whenever a timer app was checked.
   */
  readonly keepScreenAwake = this.keepScreenAwakeSignal.asReadonly();

  /** True when the device turned the request down, so the page can say the screen may still dim. */
  readonly screenWakeRefused = this.screenWake.isRefused;

  setKeepScreenAwake(keep: boolean): void {
    this.keepScreenAwakeSignal.set(keep);

    if (keep) void this.screenWake.hold();
    else this.screenWake.release();
  }

  // -------------------------------------------------------------------------
  // Loading and printing
  // -------------------------------------------------------------------------

  retryLoad(): void {
    void this.load();
  }

  print(): void {
    this.printService.print();
  }

  private async load(): Promise<void> {
    this.loadStateSignal.set({ status: 'loading' });

    // Read together and shown together: a page that appeared without its temperatures and then grew them
    // would also be a page that could be printed in between.
    const [outcome, units] = await Promise.all([
      this.recipeService.getRecipeDetail(this.workspaceSlug, this.recipeId),
      this.referenceService.listUnits(),
    ]);

    this.temperatureUnits.set(
      units.status === 'found'
        ? new Map(units.units.filter((unit) => unit.dimension === 'Temperature').map((unit) => [unit.id, unit.abbreviation]))
        : null,
    );

    if (outcome.status !== 'found') {
      this.loadStateSignal.set({ status: outcome.status });
      return;
    }

    this.loadStateSignal.set({ status: 'ready', recipe: outcome.recipe });

    // The tab's title is what a browser offers as the file name when a print is saved as a PDF.
    this.titleService.setTitle(`${outcome.recipe.title} · ${APP_NAME}`);

    if (this.route.snapshot.queryParamMap.get('print') === '1') this.printOnArrival();
  }

  /**
   * Opens the print dialog once the recipe is on screen, for a visit that arrived asking for a print.
   *
   * The parameter is taken off the address afterwards, so reloading the page or coming back to it does not
   * print a second time.
   */
  private printOnArrival(): void {
    afterNextRender(
      () => {
        this.printService.print();
        void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
      },
      { injector: this.injector },
    );
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }
    throw new Error('RecipeKitchenViewComponent route is missing a workspaceSlug segment.');
  }
}

function withMembership(ids: ReadonlySet<string>, id: string, member: boolean): ReadonlySet<string> {
  const next = new Set(ids);
  if (member) next.add(id);
  else next.delete(id);

  return next;
}
