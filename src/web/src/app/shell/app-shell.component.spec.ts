import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import { CpLoadingBarComponent, CpThemeService } from '@creator-pantry/ui';
import { EMPTY, Observable, Subject } from 'rxjs';

import { AppShellComponent, SHELL_COMPACT_QUERY } from './app-shell.component';
import { QuickCaptureComponent } from '../shared/quick-capture/quick-capture.component';
import { QuickCaptureService } from '../shared/quick-capture/quick-capture.service';
import { AuthService, SessionState } from '../services/auth.service';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';

interface FakeRouteNode {
  readonly slug?: string;
  readonly section?: string;
  readonly title?: string;
  readonly pageWidth?: string;
  readonly tone?: string;
  /** Any other route parameter the node names — a record id, say. */
  readonly params?: Readonly<Record<string, string>>;
}

/** Builds a fake ActivatedRoute descendant chain (each entry one level deeper) matching what readRouteState() walks. */
function buildRouteChain(nodes: readonly FakeRouteNode[]): { firstChild: unknown } {
  let child: { firstChild: unknown; snapshot: unknown } | null = null;
  for (let i = nodes.length - 1; i >= 0; i--) {
    const node = nodes[i];
    child = {
      firstChild: child,
      snapshot: {
        paramMap: fakeParamMap({ ...(node.slug ? { workspaceSlug: node.slug } : {}), ...(node.params ?? {}) }),
        url: node.section ? [{ path: node.section }] : [],
        data: {
          ...(node.title ? { title: node.title } : {}),
          ...(node.pageWidth ? { pageWidth: node.pageWidth } : {}),
          ...(node.tone ? { tone: node.tone } : {}),
        },
      },
    };
  }
  return { firstChild: child };
}

/** A ParamMap's two members the shell reads: one parameter by name, and the names it holds. */
function fakeParamMap(params: Readonly<Record<string, string>>) {
  return { get: (key: string) => params[key] ?? null, keys: Object.keys(params) };
}

function fakeRouteTree(slug: string | null, section: string) {
  return buildRouteChain(slug === null ? [{ section }] : [{ slug }, { section, title: 'Recipes' }]);
}

describe('AppShellComponent', () => {
  let navigateByUrlSpy: jasmine.Spy;
  let logoutSpy: jasmine.Spy;
  let ensureLoadedSpy: jasmine.Spy;
  let toggleThemeSpy: jasmine.Spy;
  let resolvedTheme: ReturnType<typeof signal<'light' | 'dark'>>;
  /** The router's navigation in progress: null between navigations, as the real `currentNavigation` is. */
  let currentNavigation: ReturnType<typeof signal<unknown>>;

  async function createFixture(
    session: SessionState,
    slug: string | null,
    section = 'recipes',
    route: { firstChild: unknown } = fakeRouteTree(slug, section),
    memberships: unknown = { status: 'loading' },
    events: Observable<unknown> = EMPTY,
  ) {
    navigateByUrlSpy = jasmine.createSpy('navigateByUrl').and.resolveTo(true);
    logoutSpy = jasmine.createSpy('logout').and.resolveTo(undefined);
    ensureLoadedSpy = jasmine.createSpy('ensureLoaded').and.resolveTo(undefined);
    resolvedTheme = signal<'light' | 'dark'>('light');
    toggleThemeSpy = jasmine
      .createSpy('toggle')
      .and.callFake(() => resolvedTheme.set(resolvedTheme() === 'dark' ? 'light' : 'dark'));
    currentNavigation = signal<unknown>(null);

    await TestBed.configureTestingModule({
      imports: [AppShellComponent],
      providers: [
        { provide: AuthService, useValue: { session: () => session, logout: logoutSpy } },
        { provide: WorkspaceMembershipService, useValue: { state: () => memberships, ensureLoaded: ensureLoadedSpy } },
        {
          provide: Router,
          useValue: {
            events,
            currentNavigation,
            navigate: jasmine.createSpy('navigate'),
            navigateByUrl: navigateByUrlSpy,
          },
        },
        { provide: ActivatedRoute, useValue: route },
        { provide: CpThemeService, useValue: { resolved: resolvedTheme, toggle: toggleThemeSpy } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AppShellComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders all 13 named sections as nav links scoped to the current workspace slug', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');

    const links = fixture.nativeElement.querySelectorAll('nav a');
    expect(links.length).toBe(13);
    expect((links[0] as HTMLAnchorElement).textContent).toContain('Dashboard');
    expect((links[4] as HTMLAnchorElement).textContent).toContain('Recipes');
  });

  it('lights the loading bar while the router is navigating, and only then', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const bar = fixture.debugElement.query(By.directive(CpLoadingBarComponent))
      .componentInstance as CpLoadingBarComponent;
    expect(bar.active()).toBeFalse();

    currentNavigation.set({ id: 2 });
    fixture.detectChanges();
    expect(bar.active()).toBeTrue();

    currentNavigation.set(null);
    fixture.detectChanges();
    expect(bar.active()).toBeFalse();
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

  it("prints no h1 of its own — the page's cp-page-head owns the one title — and still knows the route's", async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    expect(fixture.nativeElement.querySelector('h1')).toBeNull();
    expect(fixture.componentInstance.currentPageTitle()).toBe('Recipes');
  });

  describe('quick capture (13A.31)', () => {
    const SESSION: SessionState = { status: 'authenticated', displayName: 'Robert' };
    const RECIPE = '6f1c2a4e-8b1d-4c3e-9f2a-1b2c3d4e5f60';

    function membershipsAs(role: string) {
      return {
        status: 'ready',
        memberships: [
          {
            workspaceId: 'w1',
            workspaceSlug: 'cozy-fall',
            workspaceName: 'Cozy Fall',
            membershipId: 'm1',
            role,
            status: 'Active',
          },
        ],
      };
    }

    function quickCapture(
      fixture: ComponentFixture<AppShellComponent>,
    ): { componentInstance: QuickCaptureComponent } | null {
      return fixture.debugElement.query(By.directive(QuickCaptureComponent));
    }

    it('offers "+ Task" on a workspace page to anyone who can keep work there', async () => {
      const fixture = await createFixture(
        SESSION,
        'cozy-fall',
        'recipes',
        fakeRouteTree('cozy-fall', 'recipes'),
        membershipsAs('Contributor'),
      );

      expect(quickCapture(fixture)).not.toBeNull();
      const button = (fixture.nativeElement as HTMLElement).querySelector('header button[aria-label="Add a task"]');
      expect(button).toBeTruthy();
      expect(button!.getAttribute('aria-haspopup')).toBe('dialog');
    });

    it('renders the dialog after the bar, never inside it, so its scrim covers the page', async () => {
      const fixture = await createFixture(
        SESSION,
        'cozy-fall',
        'recipes',
        fakeRouteTree('cozy-fall', 'recipes'),
        membershipsAs('Owner'),
      );
      const root = fixture.nativeElement as HTMLElement;

      (root.querySelector('header button[aria-label="Add a task"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const dialog = root.querySelector('[role="dialog"]');
      expect(dialog).not.toBeNull();
      expect(dialog!.closest('header')).toBeNull();
      expect(root.querySelector('header cp-quick-capture')).toBeNull();
    });

    it('lets a page open the same dialog through QuickCaptureService, and stops when the shell goes (13A.32)', async () => {
      const fixture = await createFixture(
        SESSION,
        'cozy-fall',
        'recipes',
        fakeRouteTree('cozy-fall', 'recipes'),
        membershipsAs('Owner'),
      );
      const root = fixture.nativeElement as HTMLElement;
      const service = TestBed.inject(QuickCaptureService);

      expect(service.available).toBeTrue();
      service.open({ today: '2038-10-14' });
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const dialog = root.querySelector('[role="dialog"]');
      expect(dialog).not.toBeNull();
      expect(dialog!.closest('header')).toBeNull();

      fixture.destroy();
      expect(service.available).toBeFalse();
    });

    it('offers nothing to a Viewer, while memberships load, or where no workspace is in the URL', async () => {
      expect(
        quickCapture(
          await createFixture(
            SESSION,
            'cozy-fall',
            'recipes',
            fakeRouteTree('cozy-fall', 'recipes'),
            membershipsAs('Viewer'),
          ),
        ),
      ).toBeNull();
      TestBed.resetTestingModule();
      expect(quickCapture(await createFixture(SESSION, 'cozy-fall'))).toBeNull();
      TestBed.resetTestingModule();
      expect(
        quickCapture(
          await createFixture(SESSION, null, 'dashboard', fakeRouteTree(null, 'dashboard'), membershipsAs('Owner')),
        ),
      ).toBeNull();
    });

    it('offers to link the record on the page behind, and nothing on a page about no one record', async () => {
      const onRecipe = buildRouteChain([
        { slug: 'cozy-fall' },
        { section: 'recipes', title: 'Recipes' },
        { params: { recipeId: RECIPE } },
      ]);
      const fixture = await createFixture(SESSION, 'cozy-fall', 'recipes', onRecipe, membershipsAs('Editor'));

      expect(quickCapture(fixture)!.componentInstance.link()).toEqual(
        jasmine.objectContaining({ field: 'recipeId', id: RECIPE }),
      );

      TestBed.resetTestingModule();
      const onLibrary = await createFixture(
        SESSION,
        'cozy-fall',
        'recipes',
        fakeRouteTree('cozy-fall', 'recipes'),
        membershipsAs('Editor'),
      );
      expect(quickCapture(onLibrary)!.componentInstance.link()).toBeNull();
    });
  });

  describe('page frame', () => {
    // The spec build loads no global stylesheet, so the tokens are given values here. Deliberately not the
    // real ones: what is asserted is that the frame reads the tokens, not what tokens.scss sets them to.
    const TOKENS: Record<string, string> = {
      '--cp-page-gutter': '28px',
      // The frame's top padding. Left unset, the whole padding shorthand would be invalid and compute to 0.
      '--cp-space-8': '20px',
      '--cp-page-width-reading': '500px',
      '--cp-page-width-standard': '700px',
      '--cp-page-width-wide': '900px',
    };

    beforeEach(() => {
      for (const [name, value] of Object.entries(TOKENS)) document.documentElement.style.setProperty(name, value);
    });

    afterEach(() => {
      for (const name of Object.keys(TOKENS)) document.documentElement.style.removeProperty(name);
    });

    async function createFrame(pageWidth?: string) {
      const route = buildRouteChain([{ slug: 'cozy-fall' }, { section: 'recipes', title: 'Recipes', pageWidth }]);
      const fixture = await createFixture(
        { status: 'authenticated', displayName: 'Robert' },
        'cozy-fall',
        'recipes',
        route,
      );
      // Attached, because a detached element has no computed layout to read.
      document.body.appendChild(fixture.nativeElement);
      const frame = fixture.nativeElement.querySelector('main > .page-frame') as HTMLElement;
      return { fixture, frame, style: getComputedStyle(frame) };
    }

    it('holds the routed page, so every page starts from the same left edge', async () => {
      const { frame } = await createFrame();

      expect(frame.querySelector(':scope > router-outlet')).toBeTruthy();
    });

    it('applies the gutter token on both sides and at the bottom', async () => {
      const { style } = await createFrame();

      expect(style.paddingLeft).toBe('28px');
      expect(style.paddingRight).toBe('28px');
      expect(style.paddingBottom).toBe('28px');
    });

    it('takes the standard width when the route names none', async () => {
      const { frame, style } = await createFrame();

      expect(frame.getAttribute('data-page-width')).toBe('standard');
      expect(style.maxWidth).toBe('700px');
    });

    it("takes the width the route's data asks for", async () => {
      const wide = await createFrame('wide');
      expect(wide.frame.getAttribute('data-page-width')).toBe('wide');
      expect(wide.style.maxWidth).toBe('900px');
      TestBed.resetTestingModule();

      const reading = await createFrame('reading');
      expect(reading.frame.getAttribute('data-page-width')).toBe('reading');
      expect(reading.style.maxWidth).toBe('500px');
    });

    it('falls back to standard for a width that is not one of the three steps', async () => {
      const { frame, style } = await createFrame('enormous');

      expect(frame.getAttribute('data-page-width')).toBe('standard');
      expect(style.maxWidth).toBe('700px');
    });
  });

  // A tool's tone (AU.2.1): asked for by a route's data, set on the frame and nowhere else.
  describe('tone', () => {
    const authenticated: SessionState = { status: 'authenticated', displayName: 'Robert' };
    const studio = (tone?: string) =>
      // The tone sits on the section's parent route and the page below it names none, as in app.routes.ts.
      buildRouteChain([{ slug: 'cozy-fall' }, { section: 'image-studio', tone }, { title: 'AI Image Studio' }]);
    const frameOf = (fixture: ComponentFixture<AppShellComponent>) =>
      fixture.nativeElement.querySelector('main > .page-frame') as HTMLElement;

    it('sets no tone for a route that names none', async () => {
      const fixture = await createFixture(authenticated, 'cozy-fall', 'recipes', studio());
      expect(frameOf(fixture).hasAttribute('data-cp-tone')).toBeFalse();
    });

    it('sets the tone a parent route names on the frame, for the page below it', async () => {
      const fixture = await createFixture(authenticated, 'cozy-fall', 'image-studio', studio('pink'));
      expect(frameOf(fixture).getAttribute('data-cp-tone')).toBe('pink');
    });

    it('ignores a tone that is not one of the four, rather than reaching the DOM with it', async () => {
      const fixture = await createFixture(authenticated, 'cozy-fall', 'image-studio', studio('teal'));
      expect(frameOf(fixture).hasAttribute('data-cp-tone')).toBeFalse();
    });

    it('sets it on the frame alone, so the nav and the top bar never take it', async () => {
      const fixture = await createFixture(authenticated, 'cozy-fall', 'image-studio', studio('pink'));
      const toned = [...fixture.nativeElement.querySelectorAll('[data-cp-tone]')] as HTMLElement[];
      expect(toned).toEqual([frameOf(fixture)]);
    });

    it('clears it when the creator navigates to an untoned page, and sets it again on the way back', async () => {
      const events = new Subject<unknown>();
      const route = studio('pink');
      const fixture = await createFixture(
        authenticated,
        'cozy-fall',
        'image-studio',
        route,
        { status: 'loading' },
        events,
      );
      expect(frameOf(fixture).getAttribute('data-cp-tone')).toBe('pink');

      route.firstChild = buildRouteChain([{ slug: 'cozy-fall' }, { section: 'recipes', title: 'Recipes' }]).firstChild;
      events.next(new NavigationEnd(1, '/cozy-fall/recipes', '/cozy-fall/recipes'));
      fixture.detectChanges();
      expect(frameOf(fixture).hasAttribute('data-cp-tone')).toBeFalse();

      route.firstChild = studio('pink').firstChild;
      events.next(new NavigationEnd(2, '/cozy-fall/image-studio', '/cozy-fall/image-studio'));
      fixture.detectChanges();
      expect(frameOf(fixture).getAttribute('data-cp-tone')).toBe('pink');
    });

    it('lets a deeper route name its own tone over its parent', async () => {
      const route = buildRouteChain([
        { slug: 'cozy-fall' },
        { section: 'workflows', tone: 'blue' },
        { title: 'Long-form', tone: 'orange' },
      ]);
      const fixture = await createFixture(authenticated, 'cozy-fall', 'workflows', route);
      expect(frameOf(fixture).getAttribute('data-cp-tone')).toBe('orange');
    });
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

  it('does not throw when a child route node exists but its snapshot is not assigned yet (still activating)', async () => {
    // This component and its own '' child (the workspace gate) activate together in one navigation;
    // readRouteState() can run before the router finishes wiring the child's snapshot.
    const stillActivating = { firstChild: { firstChild: null, snapshot: undefined } };
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, null, '', stillActivating);

    expect(fixture.componentInstance.currentWorkspaceSlug()).toBeNull();
    expect(fixture.componentInstance.currentSection()).toBe('dashboard');
    expect(fixture.componentInstance.currentPageTitle()).toBe('CreatorPantry');
  });

  it('exposes aria-controls on the mobile menu toggle pointing at the sidebar id', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const toggle = fixture.nativeElement.querySelector('.icon-btn.mobile');
    const sidebar = fixture.nativeElement.querySelector('#cp-shell-sidebar');

    expect(toggle.getAttribute('aria-controls')).toBe('cp-shell-sidebar');
    expect(sidebar).toBeTruthy();
  });

  describe('the menu on a narrow screen', () => {
    /** Answers the shell's own query as a phone would, and every other query as the browser does. */
    function narrowScreen(matches = true) {
      const listeners: ((event: MediaQueryListEvent) => void)[] = [];
      const query = {
        get matches() {
          return matches;
        },
        addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => listeners.push(listener),
        removeEventListener: () => undefined,
      };
      const original = window.matchMedia.bind(window);
      spyOn(window, 'matchMedia').and.callFake((text: string) =>
        text === SHELL_COMPACT_QUERY ? (query as unknown as MediaQueryList) : original(text),
      );
      return {
        widen() {
          matches = false;
          for (const listener of listeners) listener({ matches: false } as MediaQueryListEvent);
        },
      };
    }

    const authenticated: SessionState = { status: 'authenticated', displayName: 'Robert' };

    function parts(fixture: ComponentFixture<AppShellComponent>) {
      const el = fixture.nativeElement as HTMLElement;
      return {
        toggle: el.querySelector('.icon-btn.mobile') as HTMLButtonElement,
        close: el.querySelector('.nav-close') as HTMLButtonElement,
        scrim: () => el.querySelector('.nav-scrim') as HTMLElement | null,
        topbar: el.querySelector('.topbar') as HTMLElement,
        main: el.querySelector('main') as HTMLElement,
        sidebar: el.querySelector('#cp-shell-sidebar') as HTMLElement,
      };
    }

    /**
     * The test window is wider than the line, so the stylesheet hides the menu's two buttons; shown inline here so
     * the browser lets them take focus, as it does on a phone.
     */
    function showPhoneButtons(fixture: ComponentFixture<AppShellComponent>) {
      parts(fixture).toggle.style.display = 'inline-grid';
      parts(fixture).close.style.display = 'inline-grid';
    }

    async function open(fixture: ComponentFixture<AppShellComponent>) {
      parts(fixture).toggle.click();
      fixture.detectChanges();
      await fixture.whenStable();
    }

    afterEach(() => (document.body.style.overflow = ''));

    it('opens from its button, says so, and starts the keyboard on its close button', async () => {
      narrowScreen();
      const fixture = await createFixture(authenticated, 'cozy-fall');
      document.body.appendChild(fixture.nativeElement);
      showPhoneButtons(fixture);
      const { toggle, close, sidebar } = parts(fixture);
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      await open(fixture);

      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(sidebar.classList).toContain('open');
      expect(document.activeElement).toBe(close);
      fixture.nativeElement.remove();
    });

    it('makes the bar and the page inert and holds the page still while it is open, and gives both back', async () => {
      narrowScreen();
      const fixture = await createFixture(authenticated, 'cozy-fall');
      const { topbar, main } = parts(fixture);

      await open(fixture);
      expect(topbar.hasAttribute('inert')).toBeTrue();
      expect(main.hasAttribute('inert')).toBeTrue();
      expect(document.body.style.overflow).toBe('hidden');

      parts(fixture).close.click();
      fixture.detectChanges();
      expect(topbar.hasAttribute('inert')).toBeFalse();
      expect(main.hasAttribute('inert')).toBeFalse();
      expect(document.body.style.overflow).toBe('');
    });

    it('closes from its close button and gives focus back to the menu button', async () => {
      narrowScreen();
      const fixture = await createFixture(authenticated, 'cozy-fall');
      document.body.appendChild(fixture.nativeElement);
      showPhoneButtons(fixture);
      await open(fixture);

      parts(fixture).close.click();
      fixture.detectChanges();
      await fixture.whenStable();

      expect(parts(fixture).toggle.getAttribute('aria-expanded')).toBe('false');
      expect(document.activeElement).toBe(parts(fixture).toggle);
      fixture.nativeElement.remove();
    });

    it('closes from a tap on the scrim, and from Escape', async () => {
      narrowScreen();
      const fixture = await createFixture(authenticated, 'cozy-fall');
      await open(fixture);
      expect(parts(fixture).scrim()).not.toBeNull();

      parts(fixture).scrim()!.click();
      fixture.detectChanges();
      expect(parts(fixture).toggle.getAttribute('aria-expanded')).toBe('false');
      expect(parts(fixture).scrim()).toBeNull();

      await open(fixture);
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
      expect(parts(fixture).toggle.getAttribute('aria-expanded')).toBe('false');
    });

    it('closes when a section is chosen — the page already showing included — and puts focus on the page', async () => {
      narrowScreen();
      const fixture = await createFixture(authenticated, 'cozy-fall');
      document.body.appendChild(fixture.nativeElement);
      await open(fixture);

      // The router is a stub here, so this navigates nowhere: exactly the case of the page already showing.
      (fixture.nativeElement.querySelector('nav a') as HTMLAnchorElement).click();
      fixture.detectChanges();
      await fixture.whenStable();

      expect(parts(fixture).toggle.getAttribute('aria-expanded')).toBe('false');
      expect(document.activeElement).toBe(parts(fixture).main);
      fixture.nativeElement.remove();
    });

    it('closes on any navigation, the browser’s Back included', async () => {
      narrowScreen();
      const events = new Subject<unknown>();
      const fixture = await createFixture(
        authenticated,
        'cozy-fall',
        'recipes',
        fakeRouteTree('cozy-fall', 'recipes'),
        { status: 'loading' },
        events,
      );
      await open(fixture);

      events.next(new NavigationEnd(2, '/cozy-fall/dashboard', '/cozy-fall/dashboard'));
      fixture.detectChanges();

      expect(parts(fixture).toggle.getAttribute('aria-expanded')).toBe('false');
    });

    it('puts itself away when the window widens past the line, so no lock is left on the page', async () => {
      const screen = narrowScreen();
      const fixture = await createFixture(authenticated, 'cozy-fall');
      await open(fixture);

      screen.widen();
      fixture.detectChanges();

      expect(parts(fixture).sidebar.classList).not.toContain('open');
      expect(parts(fixture).main.hasAttribute('inert')).toBeFalse();
      expect(document.body.style.overflow).toBe('');
    });

    it('holds the account — name, allowance and Sign out — at its foot, which signs out', async () => {
      narrowScreen();
      const fixture = await createFixture(authenticated, 'cozy-fall');
      const account = fixture.nativeElement.querySelector('#cp-shell-sidebar .sidebar-account') as HTMLElement;

      expect(account.textContent).toContain('Robert');
      expect(account.querySelector('a.allowance-link')?.textContent?.trim()).toBe('AI allowance');

      (account.querySelector('button') as HTMLButtonElement).click();
      await fixture.whenStable();
      expect(logoutSpy).toHaveBeenCalled();
    });

    it('makes nothing inert and locks nothing beside the wide sidebar', async () => {
      narrowScreen(false);
      const fixture = await createFixture(authenticated, 'cozy-fall');
      await open(fixture);

      expect(parts(fixture).main.hasAttribute('inert')).toBeFalse();
      expect(document.body.style.overflow).toBe('');
    });
  });

  it('marks decorative nav icons aria-hidden so only the label is announced', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const firstLink = fixture.nativeElement.querySelector('nav a');
    expect(firstLink.querySelector('span[aria-hidden="true"]')).toBeTruthy();
  });

  it('labels the theme toggle for the mode it will switch to, starting from light', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const toggle = fixture.nativeElement.querySelector('.icon-btn:not(.mobile)') as HTMLButtonElement;

    expect(toggle.getAttribute('aria-label')).toBe('Switch to dark mode');
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
  });

  it('toggles the theme service and flips its own label/pressed state when clicked', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const toggle = fixture.nativeElement.querySelector('.icon-btn:not(.mobile)') as HTMLButtonElement;

    toggle.click();
    fixture.detectChanges();

    expect(toggleThemeSpy).toHaveBeenCalled();
    expect(resolvedTheme()).toBe('dark');
    expect(toggle.getAttribute('aria-label')).toBe('Switch to light mode');
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
  });

  it('swaps the sidebar brand mark to the dark logo when the resolved theme is dark', async () => {
    const fixture = await createFixture({ status: 'authenticated', displayName: 'Robert' }, 'cozy-fall');
    const brandMark = fixture.nativeElement.querySelector('.brand-mark img') as HTMLImageElement;

    expect(brandMark.getAttribute('src')).toBe('/images/logo-400.webp');

    resolvedTheme.set('dark');
    fixture.detectChanges();

    expect(brandMark.getAttribute('src')).toBe('/images/logodark-400.webp');
  });
});
