import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from './app.routes';
import { LandingComponent } from './features/landing/landing.component';
import { AuthService, SessionState } from './services/auth.service';
import { WorkspaceMembershipService } from './services/workspace-membership.service';
import { AppShellComponent } from './shell/app-shell.component';

/**
 * L.9 verification: exercises the REAL `routes` array (real anonymousOnlyGuard/authGuard, real
 * LandingComponent/AppShellComponent) through the Angular router, rather than instantiating components
 * directly — per L.9's restriction against bypassing routing/guards. Only the two leaf services those
 * guards/components read from (AuthService, WorkspaceMembershipService) are stubbed, the same seam every
 * other guard/component spec in this suite already stubs.
 */
describe("app routing at '/'", () => {
  function configureWithSession(session: SessionState) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        { provide: AuthService, useValue: { ensureChecked: () => Promise.resolve(), session: () => session } },
        {
          provide: WorkspaceMembershipService,
          useValue: { state: () => ({ status: 'loading' }), ensureLoaded: () => Promise.resolve() },
        },
      ],
    });
  }

  it('renders the landing page for an anonymous visitor, with working /sign-up and /sign-in CTAs', async () => {
    configureWithSession({ status: 'anonymous' });

    const harness = await RouterTestingHarness.create();
    const rootComponent = await harness.navigateByUrl('/', LandingComponent);
    harness.detectChanges();

    expect(rootComponent).toBeInstanceOf(LandingComponent);
    expect(TestBed.inject(Router).url).toBe('/');

    const el = harness.routeNativeElement as HTMLElement;
    const ctas = el.querySelectorAll<HTMLAnchorElement>('.ctas a');
    expect(el.querySelector('h1')).toBeTruthy();
    expect(ctas[0]?.getAttribute('href')).toBe('/sign-up');
    expect(ctas[1]?.getAttribute('href')).toBe('/sign-in');
  });

  it('redirects an authenticated visitor away from \'/\' into the app, never rendering the marketing page', async () => {
    configureWithSession({ status: 'authenticated', displayName: 'Robert' });

    const harness = await RouterTestingHarness.create();
    const rootComponent = await harness.navigateByUrl('/');
    harness.detectChanges();

    expect(rootComponent).toBeInstanceOf(AppShellComponent);
    expect(rootComponent).not.toBeInstanceOf(LandingComponent);
    expect(TestBed.inject(Router).url).toBe('/app');
  });

  it('still renders the landing page when the session check comes back degraded, rather than trapping the visitor', async () => {
    configureWithSession({ status: 'degraded' });

    const harness = await RouterTestingHarness.create();
    const rootComponent = await harness.navigateByUrl('/', LandingComponent);
    harness.detectChanges();

    expect(rootComponent).toBeInstanceOf(LandingComponent);
    expect(TestBed.inject(Router).url).toBe('/');
  });
});
