import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { EMPTY } from 'rxjs';

import { AppShellComponent } from './app-shell.component';
import { AuthService, SessionState } from '../services/auth.service';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';

interface FakeRouteNode {
  readonly slug?: string;
  readonly section?: string;
  readonly title?: string;
}

/** Builds a fake ActivatedRoute descendant chain (each entry one level deeper) matching what readRouteState() walks. */
function buildRouteChain(nodes: readonly FakeRouteNode[]): { firstChild: unknown } {
  let child: { firstChild: unknown; snapshot: unknown } | null = null;
  for (let i = nodes.length - 1; i >= 0; i--) {
    const node = nodes[i];
    child = {
      firstChild: child,
      snapshot: {
        paramMap: { get: (key: string) => (key === 'workspaceSlug' ? (node.slug ?? null) : null) },
        url: node.section ? [{ path: node.section }] : [],
        data: node.title ? { title: node.title } : {},
      },
    };
  }
  return { firstChild: child };
}

function fakeRouteTree(slug: string | null, section: string) {
  return buildRouteChain(slug === null ? [{ section }] : [{ slug }, { section, title: 'Recipes' }]);
}

describe('AppShellComponent', () => {
  let navigateByUrlSpy: jasmine.Spy;
  let logoutSpy: jasmine.Spy;
  let ensureLoadedSpy: jasmine.Spy;

  async function createFixture(session: SessionState, slug: string | null, section = 'recipes', route: { firstChild: unknown } = fakeRouteTree(slug, section)) {
    navigateByUrlSpy = jasmine.createSpy('navigateByUrl').and.resolveTo(true);
    logoutSpy = jasmine.createSpy('logout').and.resolveTo(undefined);
    ensureLoadedSpy = jasmine.createSpy('ensureLoaded').and.resolveTo(undefined);

    await TestBed.configureTestingModule({
      imports: [AppShellComponent],
      providers: [
        { provide: AuthService, useValue: { session: () => session, logout: logoutSpy } },
        { provide: WorkspaceMembershipService, useValue: { state: () => ({ status: 'loading' }), ensureLoaded: ensureLoadedSpy } },
        { provide: Router, useValue: { events: EMPTY, navigate: jasmine.createSpy('navigate'), navigateByUrl: navigateByUrlSpy } },
        { provide: ActivatedRoute, useValue: route },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AppShellComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders all 12 named sections as nav links scoped to the current workspace slug', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');

    const links = fixture.nativeElement.querySelectorAll('nav a');
    expect(links.length).toBe(12);
    expect((links[0] as HTMLAnchorElement).textContent).toContain('Dashboard');
    expect((links[4] as HTMLAnchorElement).textContent).toContain('Recipes');
  });

  it('renders no nav links yet when no workspace slug is resolved (index route)', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, null);
    expect(fixture.nativeElement.querySelectorAll('nav a').length).toBe(0);
  });

  it('shows the display name and a sign-out action when authenticated', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    expect(fixture.nativeElement.textContent).toContain('Robert');

    const signOutButton = Array.from(fixture.nativeElement.querySelectorAll('button')).find(
      (el) => (el as HTMLElement).textContent?.trim() === 'Sign out',
    ) as HTMLButtonElement;
    expect(signOutButton).toBeTruthy();

    signOutButton.click();
    await fixture.whenStable();

    expect(logoutSpy).toHaveBeenCalled();
    expect(navigateByUrlSpy).toHaveBeenCalledWith('/sign-in');
  });

  it('loads workspace memberships on construction', async () => {
    await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    expect(ensureLoadedSpy).toHaveBeenCalled();
  });

  it('shows the dev-only design-system link (this test build is unoptimized, so isDevMode() is true)', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    expect(fixture.nativeElement.querySelector('a.dev-link')?.textContent).toContain('Design system');
  });

  it('renders an h1 from the matched route\'s data.title', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    expect(fixture.nativeElement.querySelector('h1.page-title')?.textContent?.trim()).toBe('Recipes');
  });

  it('resolves slug/section/title from a chain deeper than 2 levels, using the deepest values', async () => {
    const deepTree = buildRouteChain([
      { section: 'outer-wrapper' },
      { slug: 'deep-ws' },
      { section: 'recipes', title: 'Recipes (deep)' },
    ]);
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, null, '', deepTree);

    expect(fixture.componentInstance.currentWorkspaceSlug()).toBe('deep-ws');
    expect(fixture.componentInstance.currentSection()).toBe('recipes');
    expect(fixture.componentInstance.currentPageTitle()).toBe('Recipes (deep)');
  });

  it('exposes aria-controls on the mobile menu toggle pointing at the sidebar id', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const toggle = fixture.nativeElement.querySelector('.icon-btn.mobile');
    const sidebar = fixture.nativeElement.querySelector('#cp-shell-sidebar');

    expect(toggle.getAttribute('aria-controls')).toBe('cp-shell-sidebar');
    expect(sidebar).toBeTruthy();
  });

  it('marks decorative nav icons aria-hidden so only the label is announced', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const firstLink = fixture.nativeElement.querySelector('nav a');
    expect(firstLink.querySelector('span[aria-hidden="true"]')).toBeTruthy();
  });
});
