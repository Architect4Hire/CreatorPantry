import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { CpButtonComponent, CpFactComponent, CpFactsComponent, CpFormSectionComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { ContentChannel } from '../../../models/brand-profile.models';
import { DAM_ASSET_KIND_LABELS, DamAssetDetail } from '../../../models/dam-asset.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { BrandProfileService } from '../../../services/brand-profile.service';
import { DamAssetService } from '../../../services/dam-asset.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { DamAssetEditComponent } from '../../dam/dam-asset-edit.component';
import { DamAssetLineageComponent } from '../../dam/dam-asset-lineage.component';
import { runAssetId } from './assets-run';

type Read =
  | { readonly status: 'loading' }
  | { readonly status: 'no-picture' }
  | { readonly status: 'gone' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly asset: DamAssetDetail };

/**
 * "Describe it" and "Tag it" in "Organize your pictures" (13A.21, DAM-UI-004): the library's own edit form, in
 * place, showing the part the step is about — with, on "Describe it", where the picture came from.
 *
 * <p><strong>Continue saves.</strong> The form sends only what changed, as a merge patch quoting the token of the
 * read it was composed against. Nothing changed is nothing sent. A field the server refuses is said beside it, and
 * a picture changed by someone else meanwhile saves nothing and keeps every word typed, with the way to load the
 * latest and carry the changes onto it.</p>
 *
 * <p><strong>The picture itself is not touched here.</strong> A new file is a new version, on the first step.</p>
 */
@Component({
  selector: 'cp-assets-detail-step',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpFactComponent,
    CpFactsComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    DamAssetEditComponent,
    DamAssetLineageComponent,
    CpSkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{ read().status === 'loading' ? 'Reading your picture…' : '' }}</p>
    @switch (read().status) {
      @case ('loading') {
        <!-- The picture arriving, in its own shape. -->
        <div aria-busy="true"><cp-skeleton shape="block" pictureAspect="4 / 3" /></div>
      }
      @case ('no-picture') {
        <cp-notice role="note">
          This step works on the picture from the first step, which isn’t there yet. Go back to the first step to add
          or choose one.
        </cp-notice>
      }
      @case ('gone') {
        <cp-notice tone="warning" role="status">
          This picture isn’t in your library any more, so there is nothing to describe. Go back to the first step to
          choose another.
        </cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your picture couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (asset(); as shown) {
          @if (mode() === 'describe') {
            <!-- Which fields are needed, said once: the shell's legend says only whether the step is. -->
            <p class="legend cp-muted">Only the title is needed. Leave anything else empty to clear it.</p>
          }
          <cp-dam-asset-edit
            [inline]="true"
            [sections]="mode() === 'tags' ? 'tags' : 'describe'"
            [workspaceSlug]="workspaceSlug()"
            [asset]="shown"
            [channels]="channels()"
            (saved)="onSaved($event)"
            (refreshed)="onRefreshed($event)"
          />

          @if (mode() === 'describe') {
            <cp-form-section heading="Where it came from" intro="What made this picture, and where it is used. Read only.">
              <dl cpFacts>
                <div cpFact label="How it arrived">{{ kindLabel(shown) }}</div>
                <div cpFact label="Versions">{{ shown.versions.length }}</div>
                @if (shown.brandProfileCount) {
                  <div cpFact label="Brand">Used by your brand profile</div>
                }
              </dl>
              <cp-dam-asset-lineage [workspaceSlug]="workspaceSlug()" [asset]="shown" />
            </cp-form-section>
          }
        }
      }
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
export class AssetsDetailStepComponent implements OnInit {
  readonly mode = input.required<'describe' | 'tags'>();
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly assets = inject(DamAssetService);
  private readonly brandProfiles = inject(BrandProfileService);
  private readonly port = inject(WorkflowStepPort);
  private readonly form = viewChild(DamAssetEditComponent);

  protected readonly read = signal<Read>({ status: 'loading' });
  protected readonly channels = signal<readonly ContentChannel[]>([]);
  private gone = false;

  protected readonly asset = computed(() => {
    const read = this.read();
    return read.status === 'ready' ? read.asset : null;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    const unregister = this.port.register({
      unsaved: computed(() => this.form()?.dirty() ?? false),
      saving: computed(() => this.form()?.saving() ?? false),
      // Continue saves what changed; a refusal keeps the creator here with the reason beside the field.
      flush: () => this.form()?.saveForStep() ?? Promise.resolve(true),
      // Describing a picture is the creator's own words, and saving them again is harmless.
      autosave: () => this.form()?.saveForStep() ?? Promise.resolve(true),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
    void this.readChannels();
  }

  async load(): Promise<void> {
    const assetId = runAssetId(this.run());
    if (assetId === null) {
      this.read.set({ status: 'no-picture' });
      return;
    }

    this.read.set({ status: 'loading' });
    const outcome = await firstValueFrom(this.assets.detail(this.workspaceSlug(), assetId));
    if (this.gone) return;

    this.read.set(
      outcome.status === 'found'
        ? { status: 'ready', asset: outcome.asset }
        : { status: outcome.status === 'not_found' ? 'gone' : 'unavailable' },
    );
  }

  protected kindLabel(asset: DamAssetDetail): string {
    return DAM_ASSET_KIND_LABELS[asset.kind];
  }

  protected onSaved(asset: DamAssetDetail): void {
    this.read.set({ status: 'ready', asset });
    this.port.announce('Saved.');
  }

  protected onRefreshed(asset: DamAssetDetail): void {
    this.read.set({ status: 'ready', asset });
  }

  /** The channels a picture can be for. A list that cannot be read leaves the picture's own channel showing. */
  private async readChannels(): Promise<void> {
    const outcome = await this.brandProfiles.listContentChannels();
    if (!this.gone && outcome.status === 'found') this.channels.set(outcome.channels);
  }
}
