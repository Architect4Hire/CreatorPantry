import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import {
  CpButtonComponent,
  CpCardComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFactComponent,
  CpFactsComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { AI_FEATURE_AREAS, AiFeature, AiFeatureArea, describeAiFeature } from '../../models/ai-feature.models';
import { AiFeaturesService } from '../../services/ai-features.service';

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

interface AreaGroup {
  readonly area: AiFeatureArea;
  readonly options: readonly CpChoiceOption[];
  /** The tasks in this area that are picked, which is the value its choice group shows. */
  readonly picked: readonly string[];
  /** The same, in words, for a member who may read but not change them. */
  readonly pickedLabels: string;
}

const COULD_NOT_SAVE = 'We couldn’t save that just now. Your choices are still selected — try again.';
const NOT_ALLOWED = 'Only an Owner of this workspace can change this.';
const CHANGED_ELSEWHERE =
  'Someone else changed these settings while you were editing. Nothing was saved — reload the page and choose again.';

/**
 * The AI features panel on the workspace settings page: which AI tasks this workspace has switched on.
 *
 * A request for a feature that is off is refused with "not switched on for this workspace", and this is the
 * switch that sentence is about. Any member may read it; only an Owner may change it, and everyone else is
 * told the same answer in words rather than shown controls they cannot use.
 *
 * One Save writes the whole set, so dirty state is the set as a whole and not a flag per area.
 */
@Component({
  selector: 'cp-workspace-ai-features',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpCardComponent,
    CpChoiceGroupComponent,
    CpFactComponent,
    CpFactsComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
  ],
  templateUrl: './workspace-ai-features.component.html',
  styleUrl: './workspace-ai-features.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceAiFeaturesComponent {
  readonly workspaceSlug = input.required<string>();

  /** Whether the caller is an Owner here. The server enforces it; this only decides what is drawn. */
  readonly canEdit = input(false);

  private readonly service = inject(AiFeaturesService);

  protected readonly load = signal<LoadState>({ status: 'loading' });
  protected readonly saveState = signal<SaveState>({ status: 'idle' });

  /** The features as the server last sent them. A signal, so a clean save reads as clean. */
  protected readonly base = signal<readonly AiFeature[]>([]);

  /** The tasks picked to be on, in the order they were picked. */
  protected readonly picked = signal<readonly string[]>([]);

  protected readonly groups = computed<readonly AreaGroup[]>(() => {
    const picked = new Set(this.picked());

    return AI_FEATURE_AREAS.map((area) => {
      const features = this.base().filter((feature) => describeAiFeature(feature.task).area === area);
      const on = features.filter((feature) => picked.has(feature.task));

      return {
        area,
        options: features.map((feature) => {
          const copy = describeAiFeature(feature.task);
          return { value: feature.task, label: copy.label, hint: copy.hint || undefined };
        }),
        picked: on.map((feature) => feature.task),
        pickedLabels: on.map((feature) => describeAiFeature(feature.task).label).join(', '),
      };
    }).filter((group) => group.options.length > 0);
  });

  protected readonly dirty = computed(() => {
    const picked = new Set(this.picked());
    return this.base().some((feature) => feature.enabled !== picked.has(feature.task));
  });

  /** True until an Owner has saved a choice, while every answer here is still the site's default. */
  protected readonly onDefaults = computed(
    () => this.base().length > 0 && this.base().every((feature) => !feature.setByWorkspace),
  );

  protected readonly saving = computed(() => this.saveState().status === 'saving');
  protected readonly failureMessage = computed(() => {
    const state = this.saveState();
    return state.status === 'failed' ? state.message : '';
  });

  constructor() {
    // Reads when the workspace is known and again if it changes. Untracked, so nothing the load itself
    // touches can re-run it.
    effect(() => {
      this.workspaceSlug();
      untracked(() => void this.init());
    });
  }

  async init(): Promise<void> {
    this.load.set({ status: 'loading' });
    this.saveState.set({ status: 'idle' });

    const outcome = await this.service.get(this.workspaceSlug());
    if (outcome.status !== 'found') {
      this.load.set({ status: outcome.status });
      return;
    }

    this.seed(outcome.features);
    this.load.set({ status: 'ready' });
  }

  protected pick(group: AreaGroup, value: readonly string[]): void {
    // One group reports its own picks; every other area's are kept as they were.
    const inGroup = new Set(group.options.map((option) => option.value));
    this.picked.set([...this.picked().filter((task) => !inGroup.has(task)), ...value]);

    // A saved or failed message is about the previous choices; a new one makes it stale.
    this.saveState.set({ status: 'idle' });
  }

  protected async save(): Promise<void> {
    if (!this.canEdit() || !this.dirty() || this.saving()) return;

    this.saveState.set({ status: 'saving' });
    const outcome = await this.service.replace(this.workspaceSlug(), this.picked());

    switch (outcome.status) {
      case 'saved':
        this.seed(outcome.features);
        this.saveState.set({ status: 'saved' });
        return;
      case 'forbidden':
        this.saveState.set({ status: 'failed', message: NOT_ALLOWED });
        return;
      case 'conflict':
        this.saveState.set({ status: 'failed', message: CHANGED_ELSEWHERE });
        return;
      case 'not_found':
        this.load.set({ status: 'not_found' });
        return;
      default:
        // The picks are left as the creator made them, so trying again is one press.
        this.saveState.set({ status: 'failed', message: COULD_NOT_SAVE });
    }
  }

  private seed(features: readonly AiFeature[]): void {
    this.base.set(features);
    this.picked.set(features.filter((feature) => feature.enabled).map((feature) => feature.task));
  }
}
