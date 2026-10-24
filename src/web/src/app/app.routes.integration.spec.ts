import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';

import { routes } from './app.routes';
import { LandingComponent } from './features/landing/landing.component';
import { PlanCatalogue } from './models/plan-catalogue.models';
import { AuthService, SessionState } from './services/auth.service';
import { PlanCatalogueService } from './services/plan-catalogue.service';
import { WorkspaceMembershipService } from './services/workspace-membership.service';
import { AppShellComponent } from './shell/app-shell.component';

/** One plan is all it takes for the pricing section to be on the page, which is what /pricing needs. */
const published: PlanCatalogue = {
  currency: 'USD',
  plans: [
    {
      key: 'creator',
      name: 'Creator',
      monthlyPrice: 19,
      annualMonthlyPrice: 15,
      annualPrice: 180,
      annualSaving: 48,
      credits: 6000,
      exampleMix: '50 recipe drafts, 100 social posts and 80 pictures',
      dailyImageCap: 25,
      qualityClasses: ['draft', 'standard', 'high'],
      recommended: true,
      trialDays: null,
    },
  ],
  packs: [],
  rates: null,
};

/**
 * L.9 verification: exercises the REAL `routes` array (real anonymousOnlyGuard/authGuard, real
 * LandingComponent/AppShellComponent) through the Angular router, rather than instantiating components
 * directly — per L.9's restriction against bypassing routing/guards. Only the two leaf services those
 * guards/components read from (AuthService, WorkspaceMembershipService) are stubbed, the same seam every
 * other guard/component spec in this suite already stubs.
 */
describe("app routing at '/'", () => {
  function configureWithSession(session: SessionState, catalogue: PlanCatalogue = PlanCatalogueService.EMPTY) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        { provide: AuthService, useValue: { ensureChecked: () => Promise.resolve(), session: () => session } },
        {
          provide: WorkspaceMembershipService,
          useValue: { state: () => ({ status: 'loading' }), ensureLoaded: () => Promise.resolve() },
        },
        { provide: PlanCatalogueService, useValue: { catalogue: () => of(catalogue) } },
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

  /**
   * /pricing is the landing page opened at one of its own sections, not a second page (LP.10). Arriving there
   * also puts the reader's place in the tab order at that section, which is the half a handled jump has to do
   * for itself.
   */
  it('opens the landing page at its pricing section for /pricing', async () => {
    configureWithSession({ status: 'anonymous' }, published);

    const harness = await RouterTestingHarness.create();
    const rootComponent = await harness.navigateByUrl('/pricing', LandingComponent);
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(rootComponent).toBeInstanceOf(LandingComponent);
    expect(TestBed.inject(Router).url).toBe('/pricing');
    expect(document.activeElement?.id).toBe('pricing');
  });

  /**
   * A deployment that publishes no catalogue has no pricing section, and this address stays a working page:
   * it renders the landing page, scrolls nowhere, and says nothing about pricing at all.
   */
  it('still renders the landing page at /pricing where there is no catalogue to show', async () => {
    configureWithSession({ status: 'anonymous' });

    const harness = await RouterTestingHarness.create();
    const rootComponent = await harness.navigateByUrl('/pricing', LandingComponent);
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();

    const el = harness.routeNativeElement as HTMLElement;
    expect(rootComponent).toBeInstanceOf(LandingComponent);
    expect(el.querySelector('#pricing')).toBeNull();
    expect(el.querySelector('h1')).toBeTruthy();
  });

  it('sends an authenticated visitor at /pricing into the app, exactly as it does at \'/\'', async () => {
    configureWithSession({ status: 'authenticated', displayName: 'Robert' }, published);

    const harness = await RouterTestingHarness.create();
    const rootComponent = await harness.navigateByUrl('/pricing');
    harness.detectChanges();

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
