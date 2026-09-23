import { isDevMode } from '@angular/core';

import { authGuard } from './core/auth.guard';
import { routes } from './app.routes';
import { AppShellComponent } from './shell/app-shell.component';
import { PlaceholderSectionComponent } from './shell/placeholder-section.component';
import { WorkspaceGateComponent } from './shell/workspace-gate.component';
import { SignInComponent } from './features/sign-in/sign-in.component';

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

    for (const section of workspaceRoute.children!.filter((route) => route.path !== '')) {
      expect(await section.loadComponent!()).withContext(section.path!).toBe(PlaceholderSectionComponent);
    }

    const signInRoute = routes.find((route) => route.path === 'sign-in')!;
    expect(await signInRoute.loadComponent!()).toBe(SignInComponent);
  });
});
