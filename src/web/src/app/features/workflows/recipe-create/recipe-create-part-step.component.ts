import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { CpNoticeComponent } from '@creator-pantry/ui';

import { WorkflowRun } from '../../../models/workflow-run.models';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeEditorFocus, RecipeEditorHost } from '../../recipes/recipe-editor-host';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { linkedRecipeId } from '../recipe-steps/recipe-run';

/**
 * One part of the recipe a "Create a recipe" step is about — its ingredients, its method, or what it makes and how
 * long it takes (13A.14). The recipe editor itself, hosted on just that part (RecipeEditorHost): the same form, the
 * same save and the same checks, never a second editor.
 *
 * <p>The step keeps its work by saving the recipe. Every move the shell makes saves it first, so Continue asks the
 * server about the recipe as it now stands, and the server decides whether the part is done.</p>
 */
@Component({
  selector: 'cp-recipe-create-part-step',
  standalone: true,
  imports: [CpNoticeComponent, RecipeEditorComponent],
  providers: [RecipeEditorHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (recipeId()) {
      <cp-recipe-editor />
    } @else {
      <cp-notice tone="warning" role="status">
        There’s no recipe yet. Go back to the first step and start one.
      </cp-notice>
    }
  `,
})
export class RecipeCreatePartStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();
  readonly focus = input.required<RecipeEditorFocus>();

  private readonly host = inject(RecipeEditorHost);
  private readonly port = inject(WorkflowStepPort);
  private readonly editor = viewChild(RecipeEditorComponent);

  /** The recipe this step works on, taken once: a step is about the recipe its run made, which does not change under it. */
  protected readonly recipeId = signal<string | null>(null);

  constructor() {
    const unregister = this.port.register({
      unsaved: computed(() => this.editor()?.isDirty() ?? false),
      saving: computed(() => this.editor()?.saving() ?? false),
      savedAt: computed(() => this.editor()?.savedAt() ?? null),
      flush: () => this.editor()?.flush() ?? Promise.resolve(true),
      // Autosave keeps the words and nothing else: the re-read that follows a move would be a server
      // round trip every few seconds, and nothing on screen is waiting on it.
      autosave: async () => (await this.editor()?.flush()) ?? true,
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    // Before the editor is rendered: it reads its host once, as it is made.
    const id = linkedRecipeId(this.run());
    this.host.workspaceSlug = this.workspaceSlug();
    this.host.recipeId = id;
    this.host.focus = this.focus();
    this.recipeId.set(id);
  }
}
