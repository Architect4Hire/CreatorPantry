import { isDevMode } from '@angular/core';

import { authGuard } from './core/auth.guard';
import { routes } from './app.routes';
import { AppShellComponent } from './shell/app-shell.component';
import { PlaceholderSectionComponent } from './shell/placeholder-section.component';
import { WorkspaceGateComponent } from './shell/workspace-gate.component';
import { SignInComponent } from './features/sign-in/sign-in.component';
import { SignUpComponent } from './features/sign-up/sign-up.component';
import { ConfirmEmailComponent } from './features/confirm-email/confirm-email.component';
import { RecipeLibraryComponent } from './features/recipes/recipe-library.component';
import { RecipeEditorComponent } from './features/recipes/recipe-editor.component';
import { recipeEditorCanDeactivateGuard } from './features/recipes/recipe-editor.guard';

describe('app routes', () => {
  it('guards the shell at the root path with authGuard', () => {
    const root = routes.find((route) => route.path === '');
    expect(root?.canActivate).toEqual([authGuard]);
  });

  it("nests all 12 named sections under a ':workspaceSlug' child route", () => {
    const root = routes.find((route) => route.path === '');
    const workspaceRoute = root?.children?.find((route) => route.path === ':workspaceSlug');
    const sectionPaths = workspaceRoute?.children?.map((route) => route.path).filter((path) => path !== '');

    expect(sectionPaths).toEqual([
      'dashboard', 'workflows', 'my-day', 'my-week', 'recipes', 'brand',
      'ai-recipe-studio', 'image-studio', 'social-studio', 'dam', 'content-board', 'prompt-library',
    ]);
  });

  it("redirects an empty section path to 'dashboard'", () => {
    const root = routes.find((route) => route.path === '');
    const workspaceRoute = root?.children?.find((route) => route.path === ':workspaceSlug');
    const indexRedirect = workspaceRoute?.children?.find((route) => route.path === '');

    expect(indexRedirect?.redirectTo).toBe('dashboard');
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
    const root = routes.find((route) => route.path === '')!;
    const workspaceRoute = root.children!.find((route) => route.path === ':workspaceSlug')!;

    // Each route's loadComponent already does `.then(m => m.XComponent)`, so it resolves directly to
    // the component class itself. Compare by reference against a directly-imported class, not by
    // `.name` — the bundler can rename same-named classes duplicated across separate lazy chunks.
    expect(await root.loadComponent!()).toBe(AppShellComponent);

    const gateRoute = root.children!.find((route) => route.path === '')!;
    expect(await gateRoute.loadComponent!()).toBe(WorkspaceGateComponent);

    for (const section of workspaceRoute.children!.filter((route) => route.path !== '' && route.path !== 'recipes')) {
      expect(await section.loadComponent!()).withContext(section.path!).toBe(PlaceholderSectionComponent);
    }

    // 'recipes' is a nested grouping instead of a single placeholder: an index library route plus
    // 'new'/':recipeId' editor routes, so it resolves through its own children rather than loadComponent.
    const recipesRoute = workspaceRoute.children!.find((route) => route.path === 'recipes')!;
    const recipesLibraryRoute = recipesRoute.children!.find((route) => route.path === '')!;
    expect(await recipesLibraryRoute.loadComponent!()).toBe(RecipeLibraryComponent);

    const recipesNewRoute = recipesRoute.children!.find((route) => route.path === 'new')!;
    expect(await recipesNewRoute.loadComponent!()).toBe(RecipeEditorComponent);
    expect(recipesNewRoute.canDeactivate).toEqual([recipeEditorCanDeactivateGuard]);

    const recipeDetailRoute = recipesRoute.children!.find((route) => route.path === ':recipeId')!;
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
