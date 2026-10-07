import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CpNoticeComponent } from '@creator-pantry/ui';

import { ContentPipelineStepDefinition } from '../../models/content-pipeline.models';

/**
 * The body of a pipeline step that is not built yet.
 *
 * It offers **no action at all** — not a disabled one — so there is nothing here to mistake for a thing the
 * product can do. The step list declares the whole journey so the progress figure is honest; this is what
 * honesty looks like on the steps that have not shipped.
 */
@Component({
  selector: 'cp-content-pipeline-step-placeholder',
  standalone: true,
  imports: [CpNoticeComponent],
  template: `
    <cp-notice role="note">{{ step().comingSoon }}</cp-notice>
    <p class="cp-muted">{{ step().help }}</p>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineStepPlaceholderComponent {
  readonly step = input.required<ContentPipelineStepDefinition>();
}
