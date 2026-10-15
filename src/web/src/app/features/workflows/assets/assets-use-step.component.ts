import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal, viewChild } from '@angular/core';
import { CpFormSectionComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { DamAssetUtilization } from '../../../models/dam-asset.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { DamAssetLogUseComponent } from '../../dam/dam-asset-log-use.component';
import { DamAssetUsageHistoryComponent } from '../../dam/dam-asset-usage-history.component';
import { runAssetId } from './assets-run';

/**
 * "Note where you used it", the last step of "Organize your pictures" (13A.21, DAM-UI-004): the library's own
 * log-a-use form in place, above where the picture has already been used.
 *
 * <p><strong>Continue logs what was entered, once.</strong> The form keeps one request key across a retry of the
 * same entry, so a lost answer is not a second use. Nothing entered is nothing logged, and the step is optional.</p>
 */
@Component({
  selector: 'cp-assets-use-step',
  standalone: true,
  imports: [CpFormSectionComponent, CpNoticeComponent, DamAssetLogUseComponent, DamAssetUsageHistoryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (assetId(); as id) {
      <!-- Which fields are needed, said once: the shell's legend says only whether the step is. -->
      <p class="legend cp-muted">To log a use, platform and date are needed; the rest is optional. Leave it empty to log nothing.</p>
      <cp-dam-asset-log-use
        [inline]="true"
        [workspaceSlug]="workspaceSlug()"
        [assetId]="id"
        (logged)="onLogged($event)"
        (assetGone)="onGone()"
      />

      <cp-form-section heading="Where it has been used">
        <cp-dam-asset-usage-history [workspaceSlug]="workspaceSlug()" [assetId]="id" [reloadToken]="historyToken()" />
      </cp-form-section>
    } @else {
      <cp-notice role="note">
        This step works on the picture from the first step, which isn’t there yet. Go back to the first step to add or
        choose one.
      </cp-notice>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-8);
    }

    .legend {
      margin: 0;
    }
  `,
})
export class AssetsUseStepComponent {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly port = inject(WorkflowStepPort);
  private readonly form = viewChild(DamAssetLogUseComponent);

  protected readonly assetId = computed(() => runAssetId(this.run()));
  protected readonly historyToken = signal(0);

  constructor() {
    const unregister = this.port.register({
      unsaved: computed(() => this.form()?.dirty() ?? false),
      saving: computed(() => this.form()?.logging() ?? false),
      flush: () => this.form()?.logForStep() ?? Promise.resolve(true),
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  protected onLogged(use: DamAssetUtilization): void {
    this.historyToken.update((token) => token + 1);
    this.port.announce(`Use on ${use.platformKey} logged.`);
  }

  protected onGone(): void {
    this.historyToken.update((token) => token + 1);
  }
}
