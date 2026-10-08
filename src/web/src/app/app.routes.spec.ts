import { isDevMode } from '@angular/core';
import { Routes } from '@angular/router';

import { anonymousOnlyGuard } from './core/anonymous-only.guard';
import { authGuard } from './core/auth.guard';
import { routes } from './app.routes';
import { AppShellComponent } from './shell/app-shell.component';
import { PlaceholderSectionComponent } from './shell/placeholder-section.component';
import { WorkspaceGateComponent } from './shell/workspace-gate.component';
import { ImageStudioComponent } from './features/image-studio/image-studio.component';
import { LandingComponent } from './features/landing/landing.component';
import { WorkflowsHubComponent } from './features/workflows/workflows-hub.component';
import { SignInComponent } from './features/sign-in/sign-in.component';
import { SignUpComponent } from './features/sign-up/sign-up.component';
import { ConfirmEmailComponent } from './features/confirm-email/confirm-email.component';
import { DamAssetDetailComponent } from './features/dam/dam-asset-detail.component';
import { damAssetDetailCanDeactivateGuard } from './features/dam/dam-asset-detail.guard';
import { DamLibraryComponent } from './features/dam/dam-library.component';
import { DAM_ROUTES } from './features/dam/dam.routes';
import { PromptDetailComponent } from './features/prompt-library/prompt-detail.component';
import { PromptLibraryComponent } from './features/prompt-library/prompt-library.component';
import { PROMPT_LIBRARY_ROUTES } from './features/prompt-library/prompt-library.routes';
import { RecipeLibraryComponent } from './features/recipes/recipe-library.component';
import { RecipeEditorComponent } from './features/recipes/recipe-editor.component';
import { BrandSettingsComponent } from './features/brand/brand-settings.component';
import { BRAND_ROUTES } from './features/brand/brand.routes';
import { brandSettingsCanDeactivateGuard } from './features/brand/brand-settings.guard';
import { BrandSetupShellComponent } from './features/brand/setup/brand-setup-shell.component';
import { brandSetupCanDeactivateGuard } from './features/brand/setup/brand-setup.guard';
import { recipeEditorCanDeactivateGuard } from './features/recipes/recipe-editor.guard';
import { RECIPES_ROUTES } from './features/recipes/recipes.routes';
import { AI_RECIPE_STUDIO_ROUTES } from './features/ai/ai-recipe-studio.routes';
import { RecipeConceptStudioComponent } from './features/ai/recipe-concept-studio.component';
import { RecipeFirstDraftReviewComponent } from './features/ai/recipe-first-draft-review.component';

describe('app routes', () => {
  it('guards the public landing route with anonymousOnlyGuard, matching only the exact empty path', () => {
    const landing = routes.find((route) => route.path === '');
    expect(landing?.canActivate).toEqual([anonymousOnlyGuard]);
    expect(landing?.pathMatch).toBe('full');
  });

  it("guards 'app' and ':workspaceSlug' with authGuard", () => {
    const appRoute = routes.find((route) => route.path === 'app');
    const workspaceRoute = routes.find((route) => route.path === ':workspaceSlug');
    expect(appRoute?.canActivate).toEqual([authGuard]);
    expect(workspaceRoute?.canActivate).toEqual([authGuard]);
  });

  it("nests all 12 named sections under the ':workspaceSlug' route", () => {
    const workspaceRoute = routes.find((route) => route.path === ':workspaceSlug');
    const sectionPaths = workspaceRoute?.children?.map((route) => route.path).filter((path) => path !== '');

    expect(sectionPaths).toEqual([
      'dashboard', 'workflows', 'my-day', 'my-week', 'recipes', 'brand',
      'ai-recipe-studio', 'image-studio', 'social-studio', 'dam', 'content-board', 'prompt-library',
    ]);
  });

  it("redirects an empty section path to 'dashboard'", () => {
    const workspaceRoute = routes.find((route) => route.path === ':workspaceSlug');
    const indexRedirect = workspaceRoute?.children?.find((route) => route.path === '');

    expect(indexRedirect?.redirectTo).toBe('dashboard');
  });

  it("mounts the workspace gate as 'app''s own index route", () => {
    const appRoute = routes.find((route) => route.path === 'app');
    const gateRoute = appRoute?.children?.find((route) => route.path === '');

    expect(gateRoute?.pathMatch).toBe('full');
  });

  it('registers /sign-in ungated', () => {
    const signIn = routes.find((route) => route.path === 'sign-in');
    expect(signIn).toBeTruthy();
    expect(signIn?.canActivate).toBeUndefined();
  });

  it('registers /sign-up and /confirm-email ungated', () => {
    const signUp = routes.find((route) => route.path === 'sign-up');
    const confirmEmail = routes.find((route) => route.path === 'confirm-email');
    expect(signUp).toBeTruthy();
    expect(signUp?.canActivate).toBeUndefined();
    expect(confirmEmail).toBeTruthy();
    expect(confirmEmail?.canActivate).toBeUndefined();
  });

  it('registers the design-system showcase route only in dev mode', () => {
    const hasShowcaseRoute = routes.some((route) => route.path === 'design-system');
    expect(hasShowcaseRoute).toBe(isDevMode());
  });

  it('resolves every lazy loadComponent without throwing, to the right component class', async () => {
    const landingRoute = routes.find((route) => route.path === '')!;
    expect(await landingRoute.loadComponent!()).toBe(LandingComponent);

    const appRoute = routes.find((route) => route.path === 'app')!;
    expect(await appRoute.loadComponent!()).toBe(AppShellComponent);

    const gateRoute = appRoute.children!.find((route) => route.path === '')!;
    expect(await gateRoute.loadComponent!()).toBe(WorkspaceGateComponent);

    const workspaceRoute = routes.find((route) => route.path === ':workspaceSlug')!;
    expect(await workspaceRoute.loadComponent!()).toBe(AppShellComponent);

    // Each route's loadComponent already does `.then(m => m.XComponent)`, so it resolves directly to
    // the component class itself. Compare by reference against a directly-imported class, not by
    // `.name` — the bundler can rename same-named classes duplicated across separate lazy chunks.
    // 'workflows' is a grouping too (12.10): the hub at its index, and the Content Pipeline under it, so the
    // Workflows nav item stays active while a guided journey runs.
    for (const section of workspaceRoute.children!.filter(
      (route) =>
        route.path !== '' &&
        route.path !== 'recipes' &&
        route.path !== 'ai-recipe-studio' &&
        route.path !== 'brand' &&
        route.path !== 'workflows' &&
        route.path !== 'image-studio' &&
        route.path !== 'dam' &&
        route.path !== 'prompt-library',
    )) {
      expect(await section.loadComponent!()).withContext(section.path!).toBe(PlaceholderSectionComponent);
    }

    // 'image-studio' is the Image Studio (12.10c), no longer a placeholder. One page, so no route tree of its
    // own and no guard: every change is kept as it is made, and leaving loses nothing.
    const imageStudio = workspaceRoute.children!.find((route) => route.path === 'image-studio')!;
    expect(await imageStudio.loadComponent!()).toBe(ImageStudioComponent);
    expect(imageStudio.canDeactivate).toBeUndefined();

    // 'dam' is the DAM (12.10e, 12.10f), no longer a placeholder: the library at its index and one asset
    // beside it. The library is a read, so there is nothing to lose by leaving it and no guard.
    const damRoute = workspaceRoute.children!.find((route) => route.path === 'dam')!;
    const damChildren = (await damRoute.loadChildren!()) as Routes;
    expect(damChildren).toBe(DAM_ROUTES);
    const damLibraryRoute = damChildren.find((route) => route.path === '')!;
    expect(damLibraryRoute.pathMatch).toBe('full');
    expect(await damLibraryRoute.loadComponent!()).toBe(DamLibraryComponent);
    expect(damLibraryRoute.data?.['title']).toBe('DAM');
    expect(damLibraryRoute.canDeactivate).toBeUndefined();
    const damDetailRoute = damChildren.find((route) => route.path === ':assetId')!;
    expect(await damDetailRoute.loadComponent!()).toBe(DamAssetDetailComponent);
    // An asset's page can hold an unsaved edit or a running upload (12.10g), so leaving it is confirmed.
    expect(damDetailRoute.canDeactivate).toEqual([damAssetDetailCanDeactivateGuard]);

    // 'prompt-library' is the Prompt Library (12.10d), no longer a placeholder: the list at its index and one
    // prompt beside it. Neither is guarded — a saved prompt is immutable, so leaving loses nothing.
    const promptLibraryRoute = workspaceRoute.children!.find((route) => route.path === 'prompt-library')!;
    const promptLibraryChildren = (await promptLibraryRoute.loadChildren!()) as Routes;
    expect(promptLibraryChildren).toBe(PROMPT_LIBRARY_ROUTES);
    const promptListRoute = promptLibraryChildren.find((route) => route.path === '')!;
    expect(promptListRoute.pathMatch).toBe('full');
    expect(await promptListRoute.loadComponent!()).toBe(PromptLibraryComponent);
    expect(promptListRoute.canDeactivate).toBeUndefined();
    const promptDetailRoute = promptLibraryChildren.find((route) => route.path === ':promptRecordId')!;
    expect(await promptDetailRoute.loadComponent!()).toBe(PromptDetailComponent);
    expect(promptDetailRoute.canDeactivate).toBeUndefined();

    // 'brand' is the brand settings (11.1c), no longer a placeholder, and is guarded against losing edits.
    // It is a grouping now: the settings page at its index, and the "Create my voice" wizard (11A.22) under
    // 'setup/:step', so the Brand nav item stays active across the whole subtree.
    // The three groupings below are lazy: the app routes only name where each tree lives, and the tree
    // (with its guards) is owned by the feature, so each is resolved through `loadChildren`.
    const brandRoute = workspaceRoute.children!.find((route) => route.path === 'brand')!;
    const brandChildren = (await brandRoute.loadChildren!()) as Routes;
    expect(brandChildren).toBe(BRAND_ROUTES);
    const brandIndexRoute = brandChildren.find((route) => route.path === '')!;
    expect(await brandIndexRoute.loadComponent!()).toBe(BrandSettingsComponent);
    expect(brandIndexRoute.canDeactivate).toEqual([brandSettingsCanDeactivateGuard]);

    const setupRoute = brandChildren.find((route) => route.path === 'setup')!;
    const setupEntry = setupRoute.children!.find((route) => route.path === '')!;
    expect(setupEntry.redirectTo).toBe('goals');
    const setupStepRoute = setupRoute.children!.find((route) => route.path === ':step')!;
    expect(await setupStepRoute.loadComponent!()).toBe(BrandSetupShellComponent);
    expect(setupStepRoute.canDeactivate).toEqual([brandSetupCanDeactivateGuard]);

    // 'ai-recipe-studio' is a nested grouping like 'recipes': AIREC-001's concept form at its index, and
    // AIREC-002's first-draft review beside it, so it resolves through loadChildren rather than loadComponent.
    const aiRecipeStudioRoute = workspaceRoute.children!.find((route) => route.path === 'ai-recipe-studio')!;
    const aiRecipeStudioChildren = (await aiRecipeStudioRoute.loadChildren!()) as Routes;
    expect(aiRecipeStudioChildren).toBe(AI_RECIPE_STUDIO_ROUTES);
    const conceptStudioRoute = aiRecipeStudioChildren.find((route) => route.path === '')!;
    expect(await conceptStudioRoute.loadComponent!()).toBe(RecipeConceptStudioComponent);

    const draftReviewRoute = aiRecipeStudioChildren.find((route) => route.path === 'draft')!;
    expect(await draftReviewRoute.loadComponent!()).toBe(RecipeFirstDraftReviewComponent);

    // 'recipes' is a nested grouping instead of a single placeholder: an index library route plus
    // 'new'/':recipeId' editor routes, so it resolves through loadChildren rather than loadComponent.
    const recipesRoute = workspaceRoute.children!.find((route) => route.path === 'recipes')!;
    const recipesChildren = (await recipesRoute.loadChildren!()) as Routes;
    expect(recipesChildren).toBe(RECIPES_ROUTES);
    const recipesLibraryRoute = recipesChildren.find((route) => route.path === '')!;
    expect(await recipesLibraryRoute.loadComponent!()).toBe(RecipeLibraryComponent);

    const recipesNewRoute = recipesChildren.find((route) => route.path === 'new')!;
    expect(await recipesNewRoute.loadComponent!()).toBe(RecipeEditorComponent);
    expect(recipesNewRoute.canDeactivate).toEqual([recipeEditorCanDeactivateGuard]);

    const recipeDetailRoute = recipesChildren.find((route) => route.path === ':recipeId')!;
    expect(await recipeDetailRoute.loadComponent!()).toBe(RecipeEditorComponent);
    expect(recipeDetailRoute.canDeactivate).toEqual([recipeEditorCanDeactivateGuard]);

    const signInRoute = routes.find((route) => route.path === 'sign-in')!;
    expect(await signInRoute.loadComponent!()).toBe(SignInComponent);

    const signUpRoute = routes.find((route) => route.path === 'sign-up')!;
    expect(await signUpRoute.loadComponent!()).toBe(SignUpComponent);

    const confirmEmailRoute = routes.find((route) => route.path === 'confirm-email')!;
    expect(await confirmEmailRoute.loadComponent!()).toBe(ConfirmEmailComponent);
  });
});

describe("the Content Pipeline's place in the route tree", () => {
  const workspaceRoute = (): Routes =>
    (routes.find((route) => route.path === ':workspaceSlug')?.children ?? []) as Routes;

  it("nests the pipeline under 'workflows', so that nav item stays active while it runs", () => {
    const workflows = workspaceRoute().find((route) => route.path === 'workflows');
    const children = workflows?.children?.map((route) => route.path);

    expect(children).toEqual(['', 'content-pipeline']);
  });

  it("keeps the Workflows page as that subtree's own index, no longer a placeholder", async () => {
    const workflows = workspaceRoute().find((route) => route.path === 'workflows');
    const index = workflows?.children?.find((route) => route.path === '');

    expect(index?.pathMatch).toBe('full');
    expect(await index?.loadComponent?.()).toBe(WorkflowsHubComponent);
    expect(index?.data?.['title']).toBe('Workflows');
    // Nothing to lose by leaving: the page holds no edits of its own.
    expect(index?.canDeactivate).toBeUndefined();
  });

  it('loads the pipeline lazily, with its own step routes', async () => {
    const workflows = workspaceRoute().find((route) => route.path === 'workflows');
    const pipeline = workflows?.children?.find((route) => route.path === 'content-pipeline');
    const children = (await pipeline?.loadChildren?.()) as Routes;

    expect(children.map((route) => route.path)).toEqual(['', ':step']);
    expect(children[0].redirectTo).toBe('setup');
  });
});
