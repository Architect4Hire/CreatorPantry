import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { CpBackLinkComponent, CpPageHeadComponent } from '@creator-pantry/ui';
import { combineLatest, map } from 'rxjs';

import { injectRouteTitle } from '../../core/route-title';
import {
  WorkflowShellComponent,
  WorkflowShellDescription,
  workflowLede,
} from '../../shared/workflow-shell/workflow-shell.component';

/**
 * `/:workspaceSlug/workflows/runs/:runId` — one guided-workflow run, in the shared shell (13A.12). A flow page: its
 * head is the journey's goal and what the creator will have, from the run the shell reads.
 *
 * It names no steps: every workflow is shown through the shell's own step fallback until its journey (13A.14 on)
 * projects screens of its own.
 */
@Component({
  selector: 'cp-workflow-run-page',
  standalone: true,
  imports: [RouterLink, CpBackLinkComponent, CpPageHeadComponent, WorkflowShellComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cp-page-head [heading]="heading()" [lede]="lede()">
      <a cpBackLink cpPageHeadBreadcrumb [routerLink]="['/', params()?.slug, 'workflows']">Workflows</a>
    </cp-page-head>

    @if (params(); as here) {
      <cp-workflow-shell [workspaceSlug]="here.slug" [runId]="here.runId" (described)="described.set($event)" />
    }
  `,
})
export class WorkflowRunPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly shell = viewChild(WorkflowShellComponent);
  private readonly fallbackTitle = injectRouteTitle('Workflow');

  protected readonly described = signal<WorkflowShellDescription | null>(null);

  protected readonly heading = computed(() => this.described()?.goal ?? this.fallbackTitle);
  protected readonly lede = computed(() => workflowLede(this.described()?.expectedOutcome ?? null));

  /** The workspace and the run, from this route and the ones above it. */
  protected readonly params = toSignal(
    combineLatest(this.ancestors().map((node) => node.paramMap)).pipe(
      map((maps) => {
        const get = (key: string): string | null => maps.map((params) => params.get(key)).find((value) => !!value) ?? null;
        const slug = get('workspaceSlug');
        const runId = get('runId');
        return slug && runId ? { slug, runId } : null;
      }),
    ),
    { initialValue: null },
  );

  /** For the route's leave guard: the shell keeps the step's work and asks only when it cannot. */
  confirmLeaveIfUnsaved(): Promise<boolean> {
    return this.shell()?.confirmLeaveIfUnsaved() ?? Promise.resolve(true);
  }

  private ancestors(): ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) nodes.push(node);
    return nodes;
  }
}
