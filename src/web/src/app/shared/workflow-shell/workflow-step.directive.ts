import { Directive, TemplateRef, inject, input } from '@angular/core';

import { WorkflowStepContext } from './workflow-step-port';

/**
 * Marks a journey's screen for one step of its workflow, by the step's key:
 *
 * ```html
 * <ng-template cpWorkflowStep="ingredients" let-step let-run="run">…</ng-template>
 * ```
 *
 * The shell renders the template whose key is the run's current step. A step with none gets the shell's own
 * fallback, which links to the screen elsewhere in the product where the step is done.
 */
@Directive({ selector: 'ng-template[cpWorkflowStep]', standalone: true })
export class WorkflowStepDirective {
  readonly key = input.required<string>({ alias: 'cpWorkflowStep' });

  readonly template = inject<TemplateRef<WorkflowStepContext>>(TemplateRef);

  static ngTemplateContextGuard(_directive: WorkflowStepDirective, context: unknown): context is WorkflowStepContext {
    return true;
  }
}
