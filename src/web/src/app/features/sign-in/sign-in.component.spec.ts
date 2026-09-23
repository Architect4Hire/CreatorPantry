import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';

import { SignInComponent } from './sign-in.component';
import { AuthService, LoginOutcome, SessionState } from '../../services/auth.service';

describe('SignInComponent', () => {
  let loginSpy: jasmine.Spy<(email: string, password: string) => Promise<LoginOutcome>>;
  let navigateSpy: jasmine.Spy;

  async function createFixture(options: { session?: SessionState; queryParams?: Record<string, string> } = {}) {
    TestBed.resetTestingModule();
    loginSpy = jasmine.createSpy('login').and.resolveTo({ status: 'success' } satisfies LoginOutcome);
    navigateSpy = jasmine.createSpy('navigateByUrl').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [SignInComponent],
      providers: [
        { provide: AuthService, useValue: { login: loginSpy, session: () => options.session ?? { status: 'anonymous' } } },
        { provide: Router, useValue: { navigateByUrl: navigateSpy } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(options.queryParams ?? {}) } },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SignInComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders email and password fields', async () => {
    const fixture = await createFixture();
    expect(fixture.nativeElement.querySelector('#sign-in-email')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('#sign-in-password')).toBeTruthy();
  });

  it('shows the degraded banner only when reason=degraded is present', async () => {
    const degraded = await createFixture({ queryParams: { reason: 'degraded' } });
    expect(degraded.nativeElement.querySelector('.banner:not(.banner--error)')).toBeTruthy();

    const clean = await createFixture();
    expect(clean.nativeElement.querySelector('.banner:not(.banner--error)')).toBeFalsy();
  });

  it('redirects immediately if already authenticated', async () => {
    await createFixture({ session: { status: 'authenticated', displayName: 'Robert' } });
    expect(navigateSpy).toHaveBeenCalledWith('/');
  });

  it('submits the entered credentials and navigates home on success', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.email.set('robert@example.com');
    fixture.componentInstance.password.set('correct-password');

    await fixture.componentInstance.submit();

    expect(loginSpy).toHaveBeenCalledWith('robert@example.com', 'correct-password');
    expect(navigateSpy).toHaveBeenCalledWith('/');
  });

  it('shows an error message and does not navigate on invalid credentials', async () => {
    const fixture = await createFixture();
    loginSpy.and.resolveTo({ status: 'invalid_credentials' } satisfies LoginOutcome);

    await fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.errorMessage()).toContain("didn't work");
    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain("didn't work");
    expect(navigateSpy).not.toHaveBeenCalledWith('/');
  });

  it('shows a distinct message for an unconfirmed email', async () => {
    const fixture = await createFixture();
    loginSpy.and.resolveTo({ status: 'email_unconfirmed' } satisfies LoginOutcome);

    await fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.errorMessage()).toContain('Confirm your email');
  });

  it('disables the submit button while submitting', async () => {
    const fixture = await createFixture();
    let resolveLogin!: (outcome: LoginOutcome) => void;
    loginSpy.and.returnValue(new Promise((resolve) => (resolveLogin = resolve)));

    const submitPromise = fixture.componentInstance.submit();
    fixture.detectChanges();

    const submitButton = fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submitButton.disabled).toBeTrue();

    resolveLogin({ status: 'success' });
    await submitPromise;
  });

  it('ignores a second submit() call fired while the first is still in flight (double-click guard)', async () => {
    const fixture = await createFixture();
    let resolveLogin!: (outcome: LoginOutcome) => void;
    loginSpy.and.returnValue(new Promise((resolve) => (resolveLogin = resolve)));

    const first = fixture.componentInstance.submit();
    const second = fixture.componentInstance.submit();

    resolveLogin({ status: 'success' });
    await Promise.all([first, second]);

    expect(loginSpy).toHaveBeenCalledTimes(1);
  });
});
