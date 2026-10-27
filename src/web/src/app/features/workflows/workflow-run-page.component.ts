import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { CpBackLinkComponent, CpNoticeComponent, CpPageHeadComponent } from '@creator-pantry/ui';
import { combineLatest, map } from 'rxjs';

import { injectRouteTitle } from '../../core/route-title';
import {
  WorkflowShellComponent,
  WorkflowShellDescription,
  workflowLede,
} from '../../shared/workflow-shell/workflow-shell.component';
import { WorkflowStepDirective } from '../../shared/workflow-shell/workflow-step.directive';
import { LONG_FORM_DEFINITION_KEY, longFormLink } from '../../shared/workflow-shell/workflow-journeys';
import { RecipeCreatePartStepComponent } from './recipe-create/recipe-create-part-step.component';
import { RecipeImproveChangeStepComponent } from './recipe-improve/recipe-improve-change-step.component';
import { RecipeImproveCompareStepComponent } from './recipe-improve/recipe-improve-compare-step.component';
import { RecipeImproveDecideStepComponent } from './recipe-improve/recipe-improve-decide-step.component';
import { BrandVoiceExamplesStepComponent } from './brand-voice/brand-voice-examples-step.component';
import { BrandVoiceGuideStepComponent } from './brand-voice/brand-voice-guide-step.component';
import { BrandVoiceSetupStepComponent } from './brand-voice/brand-voice-setup-step.component';
import { BrandVoiceSwitchOnStepComponent } from './brand-voice/brand-voice-switch-on-step.component';
import {
  BRAND_VOICE_DEFINITION_KEY,
  RECIPE_CREATE_DEFINITION_KEY,
  RECIPE_IMPROVE_DEFINITION_KEY,
  RECIPE_TEST_DEFINITION_KEY,
} from './recipe-steps/recipe-run';
import { RecipeTestBasicsStepComponent } from './recipe-test/recipe-test-basics-step.component';
import { RecipeTestCookStepComponent } from './recipe-test/recipe-test-cook-step.component';
import { RecipeTestFixStepComponent } from './recipe-test/recipe-test-fix-step.component';
import { RecipeTestReadinessStepComponent } from './recipe-test/recipe-test-readiness-step.component';
import { RecipeChooseStepComponent } from './recipe-steps/recipe-choose-step.component';
import { RecipeSizesStepComponent } from './recipe-steps/recipe-sizes-step.component';
import { RecipeCreateStartStepComponent } from './recipe-create/recipe-create-start-step.component';
import { IMAGES_DEFINITION_KEY, SOCIAL_POSTS_DEFINITION_KEY } from './creative-work/creative-work-run';
import { SocialBoardStepComponent } from './social/social-board-step.component';
import { AssetsAddStepComponent } from './assets/assets-add-step.component';
import { AssetsDetailStepComponent } from './assets/assets-detail-step.component';
import { ORGANIZE_ASSETS_DEFINITION_KEY } from './assets/assets-run';
import { AssetsUseStepComponent } from './assets/assets-use-step.component';
import { PublishCheckStepComponent } from './publishworkflow/publish-check-step.component';
import { PublishChooseStepComponent } from './publishworkflow/publish-choose-step.component';
import { PublishConfirmStepComponent } from './publishworkflow/publish-confirm-step.component';
import { PLAN_AND_PUBLISH_DEFINITION_KEY } from './publishworkflow/publish-run';
import { PublishScheduleStepComponent } from './publishworkflow/publish-schedule-step.component';
import { SocialStudioStepComponent } from './social/social-studio-step.component';
import { ImagesLeadPictureStepComponent } from './images/images-lead-picture-step.component';
import { ImagesStudioStepComponent } from './images/images-studio-step.component';

/**
 * `/:workspaceSlug/workflows/runs/:runId` — one guided-workflow run, in the shared shell (13A.12). A flow page: its
 * head is the journey's goal and what the creator will have, from the run the shell reads.
 *
 * Each journey with screens of its own projects them here, one `ng-template[cpWorkflowStep]` per step, gated on the
 * run's definition — step keys repeat across journeys. A step with no screen is shown through the shell's own
 * fallback. "Create a recipe", "Improve a recipe", "Test and approve", "Create my brand voice" "Create food images",
 * "Create social posts", "Organize your pictures" and "Plan and publish content" (13A.14–13A.17,
 * 13A.19–13A.22) have screens.
 */
@Component({
  selector: 'cp-workflow-run-page',
  standalone: true,
  imports: [
    RouterLink,
    CpBackLinkComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    WorkflowShellComponent,
    WorkflowStepDirective,
    RecipeCreatePartStepComponent,
    RecipeSizesStepComponent,
    RecipeCreateStartStepComponent,
    RecipeChooseStepComponent,
    RecipeImproveChangeStepComponent,
    RecipeImproveCompareStepComponent,
    RecipeImproveDecideStepComponent,
    RecipeTestBasicsStepComponent,
    RecipeTestCookStepComponent,
    RecipeTestFixStepComponent,
    RecipeTestReadinessStepComponent,
    BrandVoiceExamplesStepComponent,
    BrandVoiceGuideStepComponent,
    BrandVoiceSetupStepComponent,
    BrandVoiceSwitchOnStepComponent,
    ImagesStudioStepComponent,
    ImagesLeadPictureStepComponent,
    SocialStudioStepComponent,
    SocialBoardStepComponent,
    AssetsAddStepComponent,
    AssetsDetailStepComponent,
    AssetsUseStepComponent,
    PublishChooseStepComponent,
    PublishCheckStepComponent,
    PublishScheduleStepComponent,
    PublishConfirmStepComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cp-page-head [heading]="heading()" [lede]="lede()">
      <a cpBackLink cpPageHeadBreadcrumb [routerLink]="['/', params()?.slug, 'workflows']">Workflows</a>
    </cp-page-head>

    @if (params(); as here) {
      @if (definitionKey() === longForm) {
        <!-- The journey is built as the writer (LF.6.3); a run of it, however it was made, points there. -->
        <cp-notice role="note">
          This is written in the blog and newsletter writer, which keeps your place as you go.
          <a [routerLink]="writerLink(here.slug)">Open the writer</a>
        </cp-notice>
      }
      <cp-workflow-shell [workspaceSlug]="here.slug" [runId]="here.runId" (described)="described.set($event)">
        @if (definitionKey() === recipeCreate) {
          <ng-template cpWorkflowStep="start" let-step let-run="run" let-slug="workspaceSlug">
            <cp-recipe-create-start-step [step]="step" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="ingredients" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-create-part-step focus="ingredients" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="method" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-create-part-step focus="method" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="yield-and-time" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-create-part-step focus="yield-and-time" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="try-sizes" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-sizes-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        } @else if (definitionKey() === recipeImprove) {
          <ng-template cpWorkflowStep="choose" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-choose-step
              [run]="run"
              [workspaceSlug]="slug"
              intro="You’ll be changing its current version. Every earlier version is kept."
            />
          </ng-template>
          <ng-template cpWorkflowStep="change" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-improve-change-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="decide" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-improve-decide-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="compare" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-improve-compare-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="try-sizes" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-sizes-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        } @else if (definitionKey() === recipeTest) {
          <ng-template cpWorkflowStep="choose" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-choose-step
              [run]="run"
              [workspaceSlug]="slug"
              intro="You’ll test its current version, exactly as it’s written."
            />
          </ng-template>
          <ng-template cpWorkflowStep="basics" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-test-basics-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="cook" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-test-cook-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="fix" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-test-fix-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="send-for-review" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-test-readiness-step mode="send-for-review" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="approve" let-run="run" let-slug="workspaceSlug">
            <cp-recipe-test-readiness-step mode="approve" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        } @else if (definitionKey() === brandVoice) {
          <ng-template cpWorkflowStep="set-up" let-run="run" let-slug="workspaceSlug">
            <cp-brand-voice-setup-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="examples" let-run="run" let-slug="workspaceSlug">
            <cp-brand-voice-examples-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="review" let-run="run" let-slug="workspaceSlug">
            <cp-brand-voice-guide-step mode="review" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="try" let-run="run" let-slug="workspaceSlug">
            <cp-brand-voice-guide-step mode="try" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="switch-on" let-run="run" let-slug="workspaceSlug">
            <cp-brand-voice-switch-on-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        } @else if (definitionKey() === images) {
          <ng-template cpWorkflowStep="about" let-run="run" let-slug="workspaceSlug">
            <cp-images-studio-step part="about" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="plan" let-run="run" let-slug="workspaceSlug">
            <cp-images-studio-step part="plan" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="make-and-keep" let-run="run" let-slug="workspaceSlug">
            <cp-images-studio-step part="make" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="lead-picture" let-run="run" let-slug="workspaceSlug">
            <cp-images-lead-picture-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        } @else if (definitionKey() === socialPosts) {
          <ng-template cpWorkflowStep="about" let-run="run" let-slug="workspaceSlug">
            <cp-social-studio-step part="about" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="write" let-run="run" let-slug="workspaceSlug">
            <cp-social-studio-step part="write" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="picture" let-run="run" let-slug="workspaceSlug">
            <cp-social-studio-step part="picture" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="board" let-run="run" let-slug="workspaceSlug">
            <cp-social-board-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        } @else if (definitionKey() === organizeAssets) {
          <ng-template cpWorkflowStep="add" let-run="run" let-slug="workspaceSlug">
            <cp-assets-add-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="describe" let-run="run" let-slug="workspaceSlug">
            <cp-assets-detail-step mode="describe" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="tag" let-run="run" let-slug="workspaceSlug">
            <cp-assets-detail-step mode="tags" [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="note-use" let-run="run" let-slug="workspaceSlug">
            <cp-assets-use-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        } @else if (definitionKey() === planAndPublish) {
          <ng-template cpWorkflowStep="choose" let-run="run" let-slug="workspaceSlug">
            <cp-publish-choose-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="check" let-run="run" let-slug="workspaceSlug">
            <cp-publish-check-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="schedule" let-run="run" let-slug="workspaceSlug">
            <cp-publish-schedule-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
          <ng-template cpWorkflowStep="confirm" let-run="run" let-slug="workspaceSlug">
            <cp-publish-confirm-step [run]="run" [workspaceSlug]="slug" />
          </ng-template>
        }
      </cp-workflow-shell>
    }
  `,
})
export class WorkflowRunPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly shell = viewChild(WorkflowShellComponent);
  private readonly fallbackTitle = injectRouteTitle('Workflow');

  protected readonly described = signal<WorkflowShellDescription | null>(null);

  protected readonly recipeCreate = RECIPE_CREATE_DEFINITION_KEY;
  protected readonly recipeImprove = RECIPE_IMPROVE_DEFINITION_KEY;
  protected readonly recipeTest = RECIPE_TEST_DEFINITION_KEY;
  protected readonly brandVoice = BRAND_VOICE_DEFINITION_KEY;
  protected readonly longForm = LONG_FORM_DEFINITION_KEY;
  protected readonly images = IMAGES_DEFINITION_KEY;
  protected readonly socialPosts = SOCIAL_POSTS_DEFINITION_KEY;
  protected readonly organizeAssets = ORGANIZE_ASSETS_DEFINITION_KEY;
  protected readonly planAndPublish = PLAN_AND_PUBLISH_DEFINITION_KEY;
  protected readonly writerLink = longFormLink;

  /** The journey being shown, once the run is read: which step screens to project. */
  protected readonly definitionKey = computed(() => this.described()?.definitionKey ?? null);

  protected readonly heading = computed(() => this.described()?.goal ?? this.fallbackTitle);
  protected readonly lede = computed(() => workflowLede(this.described()?.expectedOutcome ?? null));

  /** The workspace and the run, from this route and the ones above it. */
  protected readonly params = toSignal(
    combineLatest(this.ancestors().map((node) => node.paramMap)).pipe(
      map((maps) => {
        const get = (key: string): string | null =>
          maps.map((params) => params.get(key)).find((value) => !!value) ?? null;
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
