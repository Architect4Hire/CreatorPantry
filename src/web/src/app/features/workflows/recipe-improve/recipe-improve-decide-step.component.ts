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

import { AiProposalDispositionResult } from '../../../models/ai-proposal.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { linkedRecipeId } from '../recipe-steps/recipe-run';
import { RecipeOpenSuggestionsComponent } from './recipe-open-suggestions.component';

/**
 * "Decide on every suggestion" (13A.15): every suggestion about the recipe still waiting for an answer — from this
 * run or from before it, because the recipe's readiness counts them all — each in the recipe page's own proposal
 * panel, safety notes first. A stale one can be declined there. Done when the server's readiness check finds
 * nothing pending and no safety note unread; the screen steps Continue forward when its own list is empty — a hint,
 * never the proof.
 */
@Component({
  selector: 'cp-recipe-improve-decide-step',
  standalone: true,
  imports: [CpNoticeComponent, RecipeOpenSuggestionsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (recipeId(); as id) {
      <cp-recipe-open-suggestions
        heading="Still waiting for your answer"
        [workspaceSlug]="workspaceSlug()"
        [recipeId]="id"
        [sayWhenEmpty]="true"
        (decided)="afterDecision($event)"
        (askAgain)="askAgain()"
      />
      @if (askedAgain()) {
        <cp-notice role="status">
          To ask for a suggestion again, go Back to “Make your changes”: it will use your recipe as it is now.
        </cp-notice>
      }
    } @else {
      <cp-notice tone="warning" role="status"
        >There’s no recipe chosen yet. Go back to the first step and choose one.</cp-notice
      >
    }
  `,
})
export class RecipeImproveDecideStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly port = inject(WorkflowStepPort);
  private readonly open = viewChild(RecipeOpenSuggestionsComponent);

  protected readonly recipeId = signal<string | null>(null);
  protected readonly askedAgain = signal(false);

  constructor() {
    // Nothing of its own to keep: every answer is kept by the panel that takes it.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      ready: computed(() => this.open()?.waiting() === 0),
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    this.recipeId.set(linkedRecipeId(this.run()));
  }

  protected afterDecision(result: AiProposalDispositionResult): void {
    this.port.announce(
      result.acceptedChangeCount > 0
        ? 'Kept. Your recipe now has those changes.'
        : 'Declined. Your recipe wasn’t changed.',
    );
    // The panel that held focus leaves the list: focus goes to the step, never to nothing.
    this.port.focusStep();
  }

  protected askAgain(): void {
    this.askedAgain.set(true);
  }
}
