import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpFieldComponent } from '@creator-pantry/ui';

import { RegistrationService } from '../../services/registration.service';

@Component({
  selector: 'cp-sign-up',
  standalone: true,
  imports: [FormsModule, RouterLink, CpButtonComponent, CpFieldComponent],
  templateUrl: './sign-up.component.html',
  styleUrl: './sign-up.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignUpComponent {
  private readonly registration = inject(RegistrationService);

  readonly email = signal('');
  readonly password = signal('');
  readonly displayName = signal('');
  readonly submitting = signal(false);
  readonly submitted = signal(false);
  readonly errorMessage = signal('');
  readonly fieldErrors = signal<Readonly<Record<string, readonly string[]>>>({});

  async submit(): Promise<void> {
    if (this.submitting()) return;
    this.submitting.set(true);
    this.errorMessage.set('');
    this.fieldErrors.set({});

    const outcome = await this.registration.register(this.email(), this.password(), this.displayName());
    this.submitting.set(false);

    switch (outcome.status) {
      case 'success':
        // Deliberately the same UI whether or not the email already had an account.
        this.submitted.set(true);
        return;
      case 'invalid':
        this.fieldErrors.set(outcome.fieldErrors);
        this.errorMessage.set('Check the highlighted fields and try again.');
        return;
      case 'unavailable':
        this.errorMessage.set("We couldn't reach the server. Check your connection and try again.");
        return;
    }
  }

  fieldError(field: string): string {
    return this.fieldErrors()[field]?.[0] ?? '';
  }
}
