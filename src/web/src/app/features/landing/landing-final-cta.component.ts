import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent } from '@creator-pantry/ui';

/** The landing page's closing repeat-CTA band: same fixed /sign-up and /sign-in targets as the hero. */
@Component({
  selector: 'cp-landing-final-cta',
  standalone: true,
  imports: [RouterLink, CpButtonComponent],
  templateUrl: './landing-final-cta.component.html',
  styleUrl: './landing-final-cta.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingFinalCtaComponent {
  readonly heading = input('Ready to get your recipes organized?');
  readonly primaryCtaLabel = input('Start free');
  readonly secondaryCtaLabel = input('Sign in');
}
