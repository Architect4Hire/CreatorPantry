import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';

import { anonymousOnlyGuard } from './core/anonymous-only.guard';
// Not lazy, and measured rather than assumed (LP.11). Every other route in this file loads on demand; this one
// is the first thing a visitor who has never heard of CreatorPantry sees, and its chunk was the last round trip
// standing between the page and its LCP budget. The router cannot start fetching a lazy component until the
// route's guards have resolved, so the chunk's fetch sat strictly after the session check instead of beside it:
// a whole round trip, worth 1.0 s of LCP under Lighthouse's mobile simulation (3.2 s lazy, 2.2 s here).
// It costs 22.6 kB gzipped in the initial bundle, which a signed-in visitor also downloads once and then has
// cached forever. Script bytes on the wire went down, not up, because a dozen tiny chunks' overhead went with it.
import { LandingComponent } from './features/landing/landing.component';
import { authGuard } from './core/auth.guard';
import { socialStudioCanDeactivateGuard } from './features/social-studio/social-studio.guard';
import { themedDaysCanDeactivateGuard } from './features/workspace-settings/themed-days.guard';
import { workflowRunCanDeactivateGuard } from './features/workflows/workflow-run.guard';
import { CONTEXT_ROUTE_PARAM, CONTEXT_ROUTE_SEGMENT } from './shared/use-this-in/handoff-destinations';

const SECTION_ROUTES: Routes = [
  { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent), data: { title: 'Dashboard' } },
  // 'workflows' is the hub plus the guided journeys it lists, so the Workflows nav item stays active while
  // one is running (RouterLinkActive matches the subtree) — the same reason 'brand' owns its setup wizard.
  {
    path: 'workflows',
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/workflows/workflows-hub.component').then((m) => m.WorkflowsHubComponent), data: { title: 'Workflows' } },
      { path: 'content-pipeline', loadChildren: () => import('./features/content-pipeline/content-pipeline.routes').then((m) => m.CONTENT_PIPELINE_ROUTES) },
      // The second guided journey (B-38): long-form, with its own route and its own steps. Nothing of it is a step of the one above.
      { path: 'long-form', loadChildren: () => import('./features/long-form/long-form-pipeline.routes').then((m) => m.LONG_FORM_PIPELINE_ROUTES) },
      // One page: the run is in its query parameters, so there is no step segment and nothing to guard on leaving.
      { path: 'quick-recipe', loadComponent: () => import('./features/quick-recipe/quick-recipe.component').then((m) => m.QuickRecipeComponent), data: { title: 'Quick Recipe' } },
      // One guided run in the shared shell (13A.12). The server's current step is the only step, so there is no step segment.
      {
        path: 'runs/:runId',
        loadComponent: () => import('./features/workflows/workflow-run-page.component').then((m) => m.WorkflowRunPageComponent),
        canDeactivate: [workflowRunCanDeactivateGuard],
        data: { title: 'Workflow' },
      },
    ],
  },
  { path: 'my-day', loadComponent: () => import('./features/my-day/my-day.component').then((m) => m.MyDayComponent), data: { title: 'My Day', pageWidth: 'reading' } },
  { path: 'my-week', loadComponent: () => import('./features/my-week/my-week.component').then((m) => m.MyWeekComponent), data: { title: 'My Week', pageWidth: 'wide' } },
  // The multi-page sections ('recipes', 'brand', 'ai-recipe-studio', 'dam', 'prompt-library') own their route trees (and the guards those trees need) beside their
  // components, so the guards and pages load with the section rather than with the app shell.
  { path: 'recipes', loadChildren: () => import('./features/recipes/recipes.routes').then((m) => m.RECIPES_ROUTES) },
  { path: 'brand', loadChildren: () => import('./features/brand/brand.routes').then((m) => m.BRAND_ROUTES) },
  { path: 'ai-recipe-studio', loadChildren: () => import('./features/ai/ai-recipe-studio.routes').then((m) => m.AI_RECIPE_STUDIO_ROUTES) },
  // The studio, and the studio opened for a creative context (AF.1.4): the id is a path segment after a
  // literal `context`. One page either way: with an id it reads that context, without one it starts new work (AF.3.1).
  {
    path: 'image-studio',
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/image-studio/image-studio.component').then((m) => m.ImageStudioComponent), data: { title: 'Image Studio', pageWidth: 'wide' } },
      { path: `${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}`, loadComponent: () => import('./features/image-studio/image-studio.component').then((m) => m.ImageStudioComponent), data: { title: 'Image Studio', pageWidth: 'wide' } },
    ],
  },
  // The same two addresses as the Image Studio, for the same reason: with an id it reads that piece of work,
  // without one it starts new work. Both leave through one guard, which asks only when a post has unsaved words.
  {
    path: 'social-studio',
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/social-studio/social-studio.component').then((m) => m.SocialStudioComponent), canDeactivate: [socialStudioCanDeactivateGuard], data: { title: 'Social Studio', pageWidth: 'wide' } },
      { path: `${CONTEXT_ROUTE_SEGMENT}/:${CONTEXT_ROUTE_PARAM}`, loadComponent: () => import('./features/social-studio/social-studio.component').then((m) => m.SocialStudioComponent), canDeactivate: [socialStudioCanDeactivateGuard], data: { title: 'Social Studio', pageWidth: 'wide' } },
    ],
  },
  { path: 'dam', loadChildren: () => import('./features/dam/dam.routes').then((m) => m.DAM_ROUTES) },
  { path: 'content-board', loadChildren: () => import('./features/content-board/content-board.routes').then((m) => m.CONTENT_BOARD_ROUTES) },
  { path: 'prompt-library', loadChildren: () => import('./features/prompt-library/prompt-library.routes').then((m) => m.PROMPT_LIBRARY_ROUTES) },
  // 'settings' is the workspace's own settings page plus the pages it links to, so the nav item stays active
  // on both — the same reason 'workflows' owns its journeys.
  {
    path: 'settings',
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/workspace-settings/workspace-settings.component').then((m) => m.WorkspaceSettingsComponent), data: { title: 'Workspace settings' } },
      {
        path: 'themed-days',
        loadComponent: () => import('./features/workspace-settings/themed-days.component').then((m) => m.ThemedDaysComponent),
        canDeactivate: [themedDaysCanDeactivateGuard],
        data: { title: 'Themed days' },
      },
    ],
  },
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
];

export const routes: Routes = [
  {
    path: 'sign-in',
    loadComponent: () => import('./features/sign-in/sign-in.component').then((m) => m.SignInComponent),
    data: { title: 'Sign in' },
  },
  {
    path: 'sign-up',
    loadComponent: () => import('./features/sign-up/sign-up.component').then((m) => m.SignUpComponent),
    data: { title: 'Create your account' },
  },
  {
    path: 'confirm-email',
    loadComponent: () => import('./features/confirm-email/confirm-email.component').then((m) => m.ConfirmEmailComponent),
    data: { title: 'Confirm your email' },
  },
  ...(isDevMode()
    ? [
        {
          path: 'design-system',
          loadComponent: () =>
            import('./design-system-showcase/design-system-showcase.component').then(
              (m) => m.DesignSystemShowcaseComponent,
            ),
        },
      ]
    : []),
  // The public landing page. Anonymous-only: an already-authenticated visitor is forwarded into the app
  // instead of seeing marketing content. It renders before any workspace context exists, so it must
  // never request or expose workspace-scoped data.
  {
    path: '',
    pathMatch: 'full',
    canActivate: [anonymousOnlyGuard],
    component: LandingComponent,
  },
  // The same page at its own address, opened at the pricing section (LP.10). Not a second page: one document
  // answers both, which is also why its canonical URL in index.html names '' rather than this. Guarded exactly
  // as '' is and no more — a signed-in visitor goes to the app from here for the same reason.
  //
  // `scrollTo` is a section id the page brings into view once it exists. It can be absent: the pricing section
  // renders only where a deployment publishes a plan catalogue, and this address stays a working page either way.
  {
    path: 'pricing',
    pathMatch: 'full',
    canActivate: [anonymousOnlyGuard],
    component: LandingComponent,
    data: { title: 'Pricing', scrollTo: 'pricing' },
  },
  // 'app' hosts the signed-in shell's own index page (workspace resolution/creation) before a workspace
  // is chosen. It is therefore, like 'sign-in'/'sign-up'/etc., a literal top-level slug no real workspace
  // may use.
  {
    path: 'app',
    canActivate: [authGuard],
    loadComponent: () => import('./shell/app-shell.component').then((m) => m.AppShellComponent),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./shell/workspace-gate.component').then((m) => m.WorkspaceGateComponent), data: { title: 'Workspaces' } },
      // Account-scoped, and deliberately not one of the workspace sections: an allowance belongs to the
      // person, and the answer spans every workspace they work in. A route under ':workspaceSlug' could only
      // narrow it to one or answer about workspaces it does not name.
      { path: 'ai-usage', loadComponent: () => import('./features/ai-usage/ai-usage.component').then((m) => m.AiUsageComponent), data: { title: 'AI allowance' } },
    ],
  },
  // Must come after every literal top-level path (sign-in, app, pricing, design-system, ...):
  // ':workspaceSlug' below matches any single segment, so if this route were tried first it would swallow
  // those literal paths as a workspace slug, send them through authGuard, and redirect back into the same
  // trap — an infinite navigation loop with no console error, since nothing ever actually throws.
  //
  // The other half of that is on the server: every literal above is in WorkspacePolicy.ReservedSlugs, so no
  // workspace is ever given an address this route cannot reach. ReservedSlugMirrorTests reads this file and
  // fails until a new literal is reserved there.
  {
    path: ':workspaceSlug',
    canActivate: [authGuard],
    loadComponent: () => import('./shell/app-shell.component').then((m) => m.AppShellComponent),
    children: SECTION_ROUTES,
  },
];
