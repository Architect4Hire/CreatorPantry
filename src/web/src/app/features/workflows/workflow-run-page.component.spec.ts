import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
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
        provideHttpClient(),
        provideHttpClientTesting(),
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
        {
          provide: WorkspaceMembershipService,
          useValue: { state: signal({ status: 'loading' }), ensureLoaded: () => Promise.resolve() },
        },
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
    expect(page.textContent).toContain(
      'A saved recipe you can keep improving. You can stop at any time and pick up where you left off.',
    );
    expect(page.querySelector('a[cpBackLink]')!.getAttribute('href')).toBe('/sams-kitchen/workflows');
  });

  /** A run of `definitionKey` arriving fresh on its first step, so the step shows at once. */
  function freshOn(definitionKey: string, stepKey: string) {
    const steps = (wireRun()['steps'] as Record<string, unknown>[]).map((step) => ({
      ...step,
      status: 'NotStarted',
      completedAt: null,
    }));
    return decodeWorkflowRun(
      wireRun({
        definitionKey,
        completedStepCount: 0,
        steps: [{ ...steps[0], key: stepKey }, ...steps.slice(1)],
        links: [],
        nextAction: {
          stepKey,
          title: 'Start your recipe',
          help: 'Write it yourself, or get a first draft.',
          requirement: 'Required',
          actions: [
            {
              capabilityKey: 'recipes.new',
              name: 'Write it yourself',
              route: 'recipes/new',
              available: true,
              youCanDoIt: true,
            },
          ],
          waitingFor: [],
        },
      }),
    )!;
  }

  it('shows "Create a recipe" through its own step screens rather than links elsewhere', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('recipe.create', 'start') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-recipe-create-start-step')).not.toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
    expect(page.querySelectorAll('h1').length).toBe(1);
  });

  it('shows "Improve a recipe" through its own step screens', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('recipe.improve', 'choose') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-recipe-choose-step')).not.toBeNull();
    expect(page.querySelector('cp-recipe-create-start-step')).toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
  });

  it('shows "Test and approve" through its own step screens', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('recipe.test-and-approve', 'basics') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-recipe-test-basics-step')).not.toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
  });

  it('shows "Create my brand voice" through its own step screens', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('brand.voice', 'set-up') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-brand-voice-setup-step')).not.toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
  });

  it('shows "Create food images" through its own step screens', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('images.create', 'about') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-images-studio-step')).not.toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
  });

  it('shows "Create social posts" through its own step screens', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('social.create', 'about') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-social-studio-step')).not.toBeNull();
    expect(page.querySelector('cp-images-studio-step')).withContext('step keys repeat across journeys').toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
  });

  it('shows "Organize your pictures" through its own step screens', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('assets.organize', 'add') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-assets-add-step')).not.toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
  });

  it('shows "Plan and publish content" through its own step screens', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('publishing.plan-and-publish', 'choose') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-publish-choose-step')).not.toBeNull();
    expect(page.querySelector('cp-recipe-choose-step')).withContext('step keys repeat across journeys').toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).toBeNull();
  });

  it('keeps another journey with a step of the same name on the shell’s own links', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('recipe.test-and-approve', 'start') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    expect(page.querySelector('cp-recipe-create-start-step')).toBeNull();
    expect(page.querySelector('cp-workflow-step-fallback')).not.toBeNull();
  });

  it('points a blog-and-newsletter run at the writer, where that journey is built (13A.18)', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('content.blog-and-seo', 'source') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    const page = harness.routeNativeElement!;
    const notice = page.querySelector('cp-notice[role="note"]')!;
    expect(notice.textContent).toContain('This is written in the blog and newsletter writer');
    expect(notice.querySelector('a')!.getAttribute('href')).toBe('/sams-kitchen/workflows/long-form');
  });

  it('says nothing of the writer on any other run', async () => {
    get.and.resolveTo({ status: 'found', run: freshOn('recipe.create', 'start') });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(harness.routeNativeElement!.textContent).not.toContain('blog and newsletter writer');
  });

  it('lets the creator leave when the step holds nothing unsaved', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-1', WorkflowRunPageComponent);
    await harness.fixture.whenStable();

    await harness.navigateByUrl('/sams-kitchen/workflows/runs/run-2');

    expect(get).toHaveBeenCalledWith('sams-kitchen', 'run-2');
  });
});
