import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmService } from '../../core/confirm.service';
import { decodeWorkflowRun } from '../../models/workflow-run.models';
import { wireRun } from '../../models/workflow-run.testing';
import { WorkflowRunService } from '../../services/workflow-run.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { WorkflowRunPageComponent } from './workflow-run-page.component';
import { workflowRunCanDeactivateGuard } from './workflow-run.guard';

describe('WorkflowRunPageComponent', () => {
  let get: jasmine.Spy;

  beforeEach(() => {
    get = jasmine.createSpy('get').and.resolveTo({ status: 'found', run: decodeWorkflowRun(wireRun())! });

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: ':workspaceSlug/workflows',
            children: [
              {
                path: 'runs/:runId',
                component: WorkflowRunPageComponent,
                canDeactivate: [workflowRunCanDeactivateGuard],
                data: { title: 'Workflow' },
              },
            ],
          },
        ]),
        { provide: WorkflowRunService, useValue: { get } },
        { provide: ConfirmService, useValue: { confirm: jasmine.createSpy('confirm').and.resolveTo(true) } },
        { provide: WorkspaceMembershipService, useValue: { state: signal({ status: 'loading' }), ensureLoaded: () => Promise.resolve() } },
      ],
    });
  });

  it('reads the run named by the address and heads the page with its goal and outcome', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(get).toHaveBeenCalledWith('sams-kitchen', 'run-1');
    const page = harness.routeNativeElement!;
    expect(page.querySelectorAll('h1').length).toBe(1);
    expect(page.querySelector('h1')!.textContent).toContain('Create a recipe');
    expect(page.textContent).toContain('A saved recipe you can keep improving. You can stop at any time and pick up where you left off.');
    expect(page.querySelector('a[cpBackLink]')!.getAttribute('href')).toBe('/sams-kitchen/workflows');
  });

  it('lets the creator leave when the step holds nothing unsaved', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();

    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-2');

    expect(get).toHaveBeenCalledWith('sams-kitchen', 'run-2');
  });
});
