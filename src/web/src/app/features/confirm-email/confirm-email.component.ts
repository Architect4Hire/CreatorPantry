import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { RegistrationService } from '../../services/registration.service';

type ConfirmState = 'confirming' | 'success' | 'invalid_link' | 'unavailable' | 'missing_link';

@Component({
  selector: 'cp-confirm-email',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './confirm-email.component.html',
  styleUrl: './confirm-email.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmEmailComponent {
  private readonly registration = inject(RegistrationService);
  private readonly route = inject(ActivatedRoute);

  readonly state = signal<ConfirmState>('confirming');

  constructor() {
    void this.confirm();
  }

  private async confirm(): Promise<void> {
    const params = this.route.snapshot.queryParamMap;
    const email = params.get('email');
    const token = params.get('token');

    if (!email || !token) {
      this.state.set('missing_link');
      return;
    }

    const outcome = await this.registration.confirmEmail(email, token);
    this.state.set(outcome.status === 'success' ? 'success' : outcome.status);
  }
}
