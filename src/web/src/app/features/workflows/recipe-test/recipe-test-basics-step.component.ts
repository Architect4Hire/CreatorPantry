import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal, viewChild } from '@angular/core';
import { CpButtonComponent, CpNoticeComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { RecipeReadiness } from '../../../models/recipe-readiness.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeReadinessService } from '../../../services/recipe-readiness.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeEditorFocus, RecipeEditorHost } from '../../recipes/recipe-editor-host';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { linkedRecipeId } from '../recipe-steps/recipe-run';
import { NO_RECIPE_YET, readinessRuleMet } from './recipe-test-readiness';

/** The four things a recipe needs before it can be tested, as the readiness rules name them (TESTRUN-004). */
const BASICS: readonly { readonly ruleId: string; readonly label: string; readonly focus: RecipeEditorFocus }[] = [
  { ruleId: 'recipe.ingredients.present', label: 'What goes in', focus: 'ingredients' },
  { ruleId: 'recipe.instructions.present', label: 'The method', focus: 'method' },
  { ruleId: 'recipe.yield.stated', label: 'What it makes', focus: 'yield-and-time' },
  { ruleId: 'recipe.time.stated', label: 'How long it takes', focus: 'yield-and-time' },
];

type Checked =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly readiness: RecipeReadiness };

/**
 * "Fill in the basics" in "Test and approve" (13A.16): the server's readiness check on the four things a test needs,
 * each said as there or still needed — and, only when something is, the recipe editor opened on just that part.
 * Nothing here decides what counts: the verdicts are the readiness rules', read from the server, and so is whether
 * the step is done.
 */
@Component({
  selector: 'cp-recipe-test-basics-step',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, CpStatusPillComponent, RecipeEditorComponent],
  providers: [RecipeEditorHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (checked().status) {
      @case ('loading') {
        <p class="cp-muted" role="status">Checking your recipe…</p>
      }
      @case ('missing') {
        <cp-notice tone="warning" role="status">{{ noRecipe }}</cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your recipe couldn’t be checked just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="check()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        <div class="basics">
          <ul class="checklist" aria-label="What a test needs">
            @for (item of items(); track item.ruleId) {
              <li class="check">
                <span>{{ item.label }}</span>
                @if (item.met) {
                  <cp-status-pill tone="success">Done</cp-status-pill>
                } @else {
                  <cp-status-pill tone="warning">Still needed</cp-status-pill>
                }
              </li>
            }
          </ul>

          @if (editing()) {
            <cp-recipe-editor />
          } @else if (allMet()) {
            <p class="plain">Everything a test needs is here.</p>
          } @else {
            <!-- Found missing after the first check — changed elsewhere since: the way to fill it in. -->
            <div class="cp-actions">
              <button cpButton type="button" variant="secondary" (click)="openEditor()">Fill these in</button>
            </div>
          }
        </div>
      }
    }
  `,
  styles: `
    .basics {
      display: grid;
      gap: var(--cp-space-6);
      min-width: 0;
    }

    .checklist {
      display: grid;
      gap: var(--cp-space-2);
      margin: 0;
      padding: 0;
      list-style: none;
      max-width: var(--cp-measure-field);
    }

    .check {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--cp-space-3);
      min-height: 2.5rem;
    }

    .plain {
      margin: 0;
    }
  `,
})
export class RecipeTestBasicsStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly readiness = inject(RecipeReadinessService);
  private readonly port = inject(WorkflowStepPort);
  private readonly editorHost = inject(RecipeEditorHost);
  private readonly editor = viewChild(RecipeEditorComponent);

  protected readonly checked = signal<Checked>({ status: 'loading' });
  protected readonly noRecipe = NO_RECIPE_YET;

  /** Whether the editor is open: decided on the first check, so a save that fills a part never closes it under the creator. */
  protected readonly editing = signal(false);

  protected readonly items = computed(() => {
    const checked = this.checked();
    if (checked.status !== 'ready') return [];
    return BASICS.map((basic) => {
      return { ...basic, met: readinessRuleMet(checked.readiness, basic.ruleId) };
    });
  });

  protected readonly allMet = computed(() => this.items().length > 0 && this.items().every((item) => item.met));

  constructor() {
    const unregister = this.port.register({
      unsaved: computed(() => this.editor()?.isDirty() ?? false),
      saving: computed(() => this.editor()?.saving() ?? false),
      savedAt: computed(() => this.editor()?.savedAt() ?? null),
      flush: async () => {
        const kept = (await this.editor()?.flush()) ?? true;
        if (kept && this.editor()) await this.check();
        return kept;
      },
      // Not while edits wait: Continue keeps them, but whether they fill the gap is the server's to say.
      ready: this.allMet,
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    this.editorHost.workspaceSlug = this.workspaceSlug();
    this.editorHost.recipeId = linkedRecipeId(this.run());
    void this.check();
  }

  async check(): Promise<void> {
    const id = linkedRecipeId(this.run());
    if (id === null) {
      this.checked.set({ status: 'missing' });
      return;
    }

    if (this.checked().status !== 'ready') this.checked.set({ status: 'loading' });
    const outcome = await this.readiness.getReadiness(this.workspaceSlug(), id);
    if (outcome.status !== 'found') {
      this.checked.set({ status: outcome.status === 'not_found' ? 'missing' : 'unavailable' });
      return;
    }

    this.checked.set({ status: 'ready', readiness: outcome.readiness });

    // The first time only: open the editor on just what is missing — one part, or all of them when several are.
    if (!this.editing() && !this.checkedOnce) this.openEditor();
    this.checkedOnce = true;
  }

  private checkedOnce = false;

  /** Opens the editor on just what is missing: one part, or all of them when several are. */
  protected openEditor(): void {
    const parts = new Set(this.items().filter((item) => !item.met).map((item) => item.focus));
    if (parts.size === 0) return;
    this.editorHost.focus = parts.size === 1 ? [...parts][0] : 'all';
    this.editing.set(true);
  }
}
