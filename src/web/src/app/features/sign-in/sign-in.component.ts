import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CpButtonComponent, CpFieldComponent } from '@creator-pantry/ui';

import { AuthService, LoginOutcome } from '../../services/auth.service';

const OUTCOME_MESSAGES: Record<Exclude<LoginOutcome['status'], 'success'>, string> = {
  invalid_credentials: "That email or password didn't work. Please try again.",
  email_unconfirmed: 'Confirm your email address before signing in — check your inbox for the confirmation link.',
  rate_limited: 'Too many attempts. Wait a moment before trying again.',
  unavailable: "We couldn't reach the server. Check your connection and try again.",
};

@Component({
  selector: 'cp-sign-in',
  standalone: true,
  imports: [FormsModule, RouterLink, CpButtonComponent, CpFieldComponent],
  templateUrl: './sign-in.component.html',
  styleUrl: './sign-in.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly email = signal('');
  readonly password = signal('');
  readonly submitting = signal(false);
  readonly errorMessage = signal('');

  readonly degraded = this.route.snapshot.queryParamMap.get('reason') === 'degraded';

  constructor() {
    if (this.auth.session().status === 'authenticated') {
      void this.router.navigateByUrl('/app');
    }
  }

  async submit(): Promise<void> {
    if (this.submitting()) return;
    this.submitting.set(true);
    this.errorMessage.set('');

    const outcome = await this.auth.login(this.email(), this.password());
    this.submitting.set(false);

    if (outcome.status === 'success') {
      await this.router.navigateByUrl('/app');
      return;
    }
    this.errorMessage.set(OUTCOME_MESSAGES[outcome.status]);
  }
}
