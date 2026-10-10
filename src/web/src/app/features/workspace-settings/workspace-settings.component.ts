import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import {
  CpButtonComponent,
  CpCardComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { Workspace, WorkspaceMeasurementSystem, isWorkspaceMeasurementSystem } from '../../models/workspace.models';
import { WorkspaceSettingsService } from '../../services/workspace-settings.service';

type LoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready' };

type SaveState =
  | { readonly status: 'idle' }
  | { readonly status: 'saving' }
  | { readonly status: 'saved' }
  | { readonly status: 'failed'; readonly message: string };

const SYSTEM_OPTIONS: readonly CpChoiceOption[] = [
  { value: 'UsCustomary', label: 'US customary', hint: 'Cups, tablespoons, ounces and pounds, with oven temperatures in °F.' },
  { value: 'Metric', label: 'Metric', hint: 'Grams, millilitres and litres, with oven temperatures in °C.' },
];

const COULD_NOT_SAVE = 'We couldn’t save that just now. Your choice is still selected — try again.';
const NOT_ALLOWED = 'Only an Owner of this workspace can change this.';

/**
 * The workspace's own settings (`:workspaceSlug/settings`). One today: the measurement system new AI recipe
 * drafts are written in (B-08).
 *
 * Any member may read it; only an Owner may change it, and everyone else sees the same answer without the
 * controls that would let them try.
 */
@Component({
  selector: 'cp-workspace-settings',
  standalone: true,
  imports: [CpButtonComponent, CpCardComponent, CpChoiceGroupComponent, CpFormSectionComponent, CpNoticeComponent],
  templateUrl: './workspace-settings.component.html',
  styleUrl: './workspace-settings.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceSettingsComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly settings = inject(WorkspaceSettingsService);

  protected readonly workspaceSlug = this.resolveWorkspaceSlug();
  protected readonly options = SYSTEM_OPTIONS;

  protected readonly load = signal<LoadState>({ status: 'loading' });
  protected readonly saveState = signal<SaveState>({ status: 'idle' });

  /** The workspace as the server last sent it. A signal, so a clean save reads as clean. */
  protected readonly base = signal<Workspace | null>(null);
  protected readonly picked = signal<WorkspaceMeasurementSystem>('UsCustomary');

  protected readonly canEdit = computed(() => this.base()?.role === 'Owner');
  protected readonly dirty = computed(() => {
    const base = this.base();
    return base !== null && base.defaultMeasurementSystem !== this.picked();
  });
  protected readonly saving = computed(() => this.saveState().status === 'saving');
  protected readonly failureMessage = computed(() => {
    const state = this.saveState();
    return state.status === 'failed' ? state.message : '';
  });

  /** What a member who cannot change the setting is told it is, in words rather than a disabled control. */
  protected readonly currentLabel = computed(
    () => SYSTEM_OPTIONS.find((option) => option.value === this.base()?.defaultMeasurementSystem)?.label ?? '',
  );

  protected readonly pickedValue = computed<readonly string[]>(() => [this.picked()]);

  constructor() {
    void this.init();
  }

  async init(): Promise<void> {
    this.load.set({ status: 'loading' });
    this.saveState.set({ status: 'idle' });

    const outcome = await this.settings.get(this.workspaceSlug);
    if (outcome.status !== 'found') {
      this.load.set({ status: outcome.status });
      return;
    }

    this.base.set(outcome.workspace);
    this.picked.set(outcome.workspace.defaultMeasurementSystem);
    this.load.set({ status: 'ready' });
  }

  protected pick(value: readonly string[]): void {
    const next = value[value.length - 1];
    if (!isWorkspaceMeasurementSystem(next)) return;

    this.picked.set(next);
    // A saved or failed message is about the previous choice; a new one makes it stale.
    this.saveState.set({ status: 'idle' });
  }

  protected async save(): Promise<void> {
    if (!this.canEdit() || !this.dirty() || this.saving()) return;

    this.saveState.set({ status: 'saving' });
    const outcome = await this.settings.setMeasurementSystem(this.workspaceSlug, this.picked());

    switch (outcome.status) {
      case 'saved':
        this.base.set(outcome.workspace);
        this.picked.set(outcome.workspace.defaultMeasurementSystem);
        this.saveState.set({ status: 'saved' });
        return;
      case 'forbidden':
        this.saveState.set({ status: 'failed', message: NOT_ALLOWED });
        return;
      case 'not_found':
        this.load.set({ status: 'not_found' });
        return;
      default:
        // The pick is left as the creator made it, so trying again is one press.
        this.saveState.set({ status: 'failed', message: COULD_NOT_SAVE });
    }
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }
    throw new Error('WorkspaceSettingsComponent route is missing a workspaceSlug segment.');
  }
}
