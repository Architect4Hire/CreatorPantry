import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent } from '@creator-pantry/ui';

import { WorkflowNextAction } from '../../models/workflow-run.models';

/**
 * A step no journey has a screen for yet. It says where the step is done in the product, and links there: the
 * creator does the work on that screen, comes back, and Continue asks the server whether the step is done. Nothing
 * here is a form, and nothing here decides completion.
 */
@Component({
  selector: 'cp-workflow-step-fallback',
  standalone: true,
  imports: [RouterLink, CpButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (doable().length > 0) {
      <p class="lead"
        >This step is done on {{ doable().length === 1 ? 'its own page' : 'one of these pages' }}. Come back here when
        you’ve finished, and carry on.</p
      >
      <div class="cp-actions">
        @for (action of doable(); track action.capabilityKey) {
          <a cpButton variant="secondary" [routerLink]="['/', workspaceSlug(), action.route]">{{ action.name }}</a>
        }
      </div>
    } @else if (someoneElse()) {
      <p class="lead">Someone with more access to this workspace needs to do this step.</p>
    } @else {
      <p class="lead">This step can’t be done from here yet.</p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .lead {
      margin: 0;
    }
  `,
})
export class WorkflowStepFallbackComponent {
  readonly step = input.required<WorkflowNextAction>();
  readonly workspaceSlug = input.required<string>();

  /** The step's actions the reader can take and that have an address, main action first. */
  protected readonly doable = computed(() =>
    this.step().actions.filter(
      (action): action is typeof action & { route: string } =>
        action.available && action.youCanDoIt && action.route !== null,
    ),
  );

  protected readonly someoneElse = computed(() =>
    this.step().actions.some((action) => action.available && !action.youCanDoIt),
  );
}
