import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { ConfirmEmailComponent } from './confirm-email.component';
import { ConfirmEmailOutcome, RegistrationService } from '../../services/registration.service';

describe('ConfirmEmailComponent', () => {
  let confirmSpy: jasmine.Spy<(email: string, token: string) => Promise<ConfirmEmailOutcome>>;

  async function createFixture(
    queryParams: Record<string, string> = { email: 'robert@example.com', token: 'a-token' },
    outcome: ConfirmEmailOutcome = { status: 'success' },
  ) {
    TestBed.resetTestingModule();
    confirmSpy = jasmine.createSpy('confirmEmail').and.resolveTo(outcome);

    await TestBed.configureTestingModule({
      imports: [ConfirmEmailComponent],
      providers: [
        provideRouter([]),
        { provide: RegistrationService, useValue: { confirmEmail: confirmSpy } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ConfirmEmailComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('confirms using the email and token query params and shows the success state', async () => {
    const fixture = await createFixture();

    expect(confirmSpy).toHaveBeenCalledWith('robert@example.com', 'a-token');
    expect(fixture.componentInstance.state()).toBe('success');
    expect(fixture.nativeElement.querySelector('.confirm-email-card')?.textContent).toContain('confirmed');
  });

  it('shows the missing-link state and never calls the service when a param is absent', async () => {
    const fixture = await createFixture({ email: 'robert@example.com' }); // no token

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(fixture.componentInstance.state()).toBe('missing_link');
  });

  it('shows the invalid-link state for a bad or expired token', async () => {
    const fixture = await createFixture(undefined, { status: 'invalid_link' });

    expect(fixture.componentInstance.state()).toBe('invalid_link');
    expect(fixture.nativeElement.querySelector('.confirm-email-card')?.textContent).toContain("isn't valid");
  });

  it('shows the unavailable state when the server cannot be reached', async () => {
    const fixture = await createFixture(undefined, { status: 'unavailable' });

    expect(fixture.componentInstance.state()).toBe('unavailable');
    expect(fixture.nativeElement.querySelector('.confirm-email-card')?.textContent).toContain("couldn't confirm");
  });
});
