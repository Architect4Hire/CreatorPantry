import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { CpEmptyStateComponent } from '@creator-pantry/ui';

/** Generic stand-in for a shell section whose feature hasn't been built yet — parametrized entirely by route `data`. */
@Component({
  selector: 'cp-placeholder-section',
  standalone: true,
  imports: [CpEmptyStateComponent],
  template: `<cp-empty-state [title]="title" [description]="description" icon="✦" />`,
  styles: [`:host{display:block;padding:var(--cp-space-8)}`],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlaceholderSectionComponent {
  private readonly route = inject(ActivatedRoute);

  readonly title: string = this.route.snapshot.data['title'] ?? 'Coming soon';
  readonly description: string = this.route.snapshot.data['description'] ?? "This section hasn't been built yet.";
}
