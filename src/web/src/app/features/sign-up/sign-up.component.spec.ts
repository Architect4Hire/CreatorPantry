import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { SignUpComponent } from './sign-up.component';
import { RegisterOutcome, RegistrationService } from '../../services/registration.service';

describe('SignUpComponent', () => {
  let registerSpy: jasmine.Spy<(email: string, password: string, displayName: string) => Promise<RegisterOutcome>>;

  async function createFixture() {
    TestBed.resetTestingModule();
    registerSpy = jasmine.createSpy('register').and.resolveTo({ status: 'success' } satisfies RegisterOutcome);

    await TestBed.configureTestingModule({
      imports: [SignUpComponent],
      providers: [provideRouter([]), { provide: RegistrationService, useValue: { register: registerSpy } }],
    }).compileComponents();

    const fixture = TestBed.createComponent(SignUpComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders display name, email, and password fields', async () => {
    const fixture = await createFixture();
    expect(fixture.nativeElement.querySelector('#sign-up-display-name')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('#sign-up-email')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('#sign-up-password')).toBeTruthy();
  });

  it('submits the entered fields and shows the check-your-email state on success', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.displayName.set('Robert');
    fixture.componentInstance.email.set('robert@example.com');
    fixture.componentInstance.password.set('correct horse battery');

    await fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(registerSpy).toHaveBeenCalledWith('robert@example.com', 'correct horse battery', 'Robert');
    expect(fixture.componentInstance.submitted()).toBeTrue();
    expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain('Check your email');
    expect(fixture.nativeElement.querySelector('form')).toBeFalsy();
  });

  it('shows the same success state whether or not the email was already registered (no enumeration)', async () => {
    const fixture = await createFixture();
    // The service always resolves 'success' regardless of duplicate status; the component has no way
    // to distinguish, and must not attempt to.
    await fixture.componentInstance.submit();
    expect(fixture.componentInstance.submitted()).toBeTrue();
  });

  it('shows field-level errors on an invalid submission and does not show the success state', async () => {
    const fixture = await createFixture();
    registerSpy.and.resolveTo({
      status: 'invalid',
      fieldErrors: { email: ['Enter a valid email address.'], password: ['Use at least 12 characters.'] },
    } satisfies RegisterOutcome);

    await fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.submitted()).toBeFalse();
    expect(fixture.componentInstance.fieldError('email')).toBe('Enter a valid email address.');
    expect(fixture.componentInstance.fieldError('password')).toBe('Use at least 12 characters.');
    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain('highlighted fields');
  });

  it('shows a generic error when the server is unreachable', async () => {
    const fixture = await createFixture();
    registerSpy.and.resolveTo({ status: 'unavailable' } satisfies RegisterOutcome);

    await fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.errorMessage()).toContain("couldn't reach the server");
  });

  it('disables the submit button while submitting', async () => {
    const fixture = await createFixture();
    let resolveRegister!: (outcome: RegisterOutcome) => void;
    registerSpy.and.returnValue(new Promise((resolve) => (resolveRegister = resolve)));

    const submitPromise = fixture.componentInstance.submit();
    fixture.detectChanges();

    const submitButton = fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submitButton.disabled).toBeTrue();

    resolveRegister({ status: 'success' });
    await submitPromise;
  });

  it('ignores a second submit() call fired while the first is still in flight', async () => {
    const fixture = await createFixture();
    let resolveRegister!: (outcome: RegisterOutcome) => void;
    registerSpy.and.returnValue(new Promise((resolve) => (resolveRegister = resolve)));

    const first = fixture.componentInstance.submit();
    const second = fixture.componentInstance.submit();

    resolveRegister({ status: 'success' });
    await Promise.all([first, second]);

    expect(registerSpy).toHaveBeenCalledTimes(1);
  });
});
