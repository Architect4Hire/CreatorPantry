import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { WorkflowRunService } from '../../services/workflow-run.service';
import { workflowRunLink } from './workflow-return';

/**
 * The way back to a guided run, on a product page the run deep-linked into (13A.17). Not the page's breadcrumb — that
 * stays the page's parent in the route tree — but a line in the page's message place naming the journey by its own
 * goal, or, as `action`, the one button a finished page offers to carry on with it.
 *
 * <p>It reads the run only for its goal. A run that cannot be read is still linked to, under a plain name, and the run
 * page says the rest: an id that is not the reader's answers there as not found.</p>
 */
@Component({
  selector: 'cp-workflow-return-notice',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpNoticeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (mode() === 'action') {
      <a cpButton variant="primary" [routerLink]="link()">Go to {{ goal() ?? 'your guided journey' }}</a>
    } @else {
      <cp-notice role="note">
        You came here from “{{ goal() ?? 'your guided journey' }}”. <a [routerLink]="link()">Carry on with it</a> when
        you’re done here.
      </cp-notice>
    }
  `,
})
export class WorkflowReturnNoticeComponent {
  readonly workspaceSlug = input.required<string>();
  readonly runId = input.required<string>();
  /** A line in the page's messages, or the primary action on a page that has finished its part. */
  readonly mode = input<'notice' | 'action'>('notice');

  private readonly runs = inject(WorkflowRunService);

  protected readonly goal = signal<string | null>(null);
  protected readonly link = computed(() => workflowRunLink(this.workspaceSlug(), this.runId()));

  constructor() {
    effect(() => {
      const slug = this.workspaceSlug();
      const id = this.runId();
      untracked(() => void this.readGoal(slug, id));
    });
  }

  private async readGoal(slug: string, id: string): Promise<void> {
    const outcome = await this.runs.get(slug, id);
    this.goal.set(outcome.status === 'found' ? outcome.run.goal : null);
  }
}
