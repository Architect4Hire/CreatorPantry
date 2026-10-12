import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';

import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpBackLinkComponent,
  CpBadgeComponent,
  CpButtonComponent,
  CpButtonSize,
  CpButtonVariant,
  CpCardComponent,
  CpCardGridComponent,
  CpCheckboxComponent,
  CpChipComponent,
  CpChipGroupComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpComboboxComponent,
  CpComboboxOption,
  CpDialogComponent,
  CpDiffLegendComponent,
  CpDrawerComponent,
  CpEmptyStateComponent,
  CpFactComponent,
  CpFactsComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpListShellComponent,
  CpListShellState,
  CpMediaCardComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpProgressComponent,
  CpProgressTone,
  CpQuickActionComponent,
  CpSearchFieldComponent,
  CpSelectFieldComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpStep,
  CpStepperComponent,
  CpTabPanelComponent,
  CpTabsComponent,
  CpToast,
  CpToastRegionComponent,
  CpToastSeverity,
  CpTone,
  CpToolbarComponent,
  CpTopicCardComponent,
  CpUploadItem,
  CpUploaderComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../core/confirm.service';
import { BrandGuideChannelRule, BrandGuideChoice, BrandGuideSelection } from '../models/brand-guide-control.models';
import { BrandGuideControlComponent } from '../shared/brand-guide-control/brand-guide-control.component';
import { BrandVisualStyleControlComponent } from '../shared/brand-visual-style-control/brand-visual-style-control.component';
import { BrandVisualReference, BrandVisualStyleLine } from '../models/brand-visual-style-control.models';

type QuickActionTone = Exclude<CpTone, 'neutral'>;

@Component({
  selector: 'cp-design-system-showcase',
  standalone: true,
  imports: [
    CpPageHeadComponent,
    CpBackLinkComponent,
    CpCardGridComponent,
    CpMediaCardComponent,
    CpTopicCardComponent,
    CpFactsComponent,
    CpFactComponent,
    CpSearchFieldComponent,
    CpSelectFieldComponent,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpDialogComponent,
    CpDrawerComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    CpProgressComponent,
    CpQuickActionComponent,
    CpStatusPillComponent,
    CpListShellComponent,
    CpTabsComponent,
    CpTabPanelComponent,
    CpToolbarComponent,
    CpEmptyStateComponent,
    CpUploaderComponent,
    CpAnchorNavComponent,
    CpCheckboxComponent,
    CpChipComponent,
    CpChipGroupComponent,
    CpChoiceGroupComponent,
    CpNoticeComponent,
    CpComboboxComponent,
    CpDiffLegendComponent,
    CpToastRegionComponent,
    BrandGuideControlComponent,
    BrandVisualStyleControlComponent,
    CpStepperComponent,
  ],
  templateUrl: './design-system-showcase.component.html',
  styleUrl: './design-system-showcase.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DesignSystemShowcaseComponent {
  /** The stepper example: Back and Continue move through it, and the second step was skipped. */
  protected readonly stepperNames = ['What you make', 'Your examples', 'Your style', 'Finish'];
  protected readonly stepperAt = signal(2);
  protected readonly stepperSteps = computed<readonly CpStep[]>(() =>
    this.stepperNames.map((label, index) => ({
      id: label,
      label,
      status: index === this.stepperAt() ? 'current' : index > this.stepperAt() ? 'upcoming' : index === 1 ? 'skipped' : 'done',
    })),
  );

  protected moveStepper(by: number): void {
    this.stepperAt.update((at) => Math.min(this.stepperNames.length - 1, Math.max(0, at + by)));
  }

  /** The chips example: several statuses may be on at once, and the page holds which. */
  protected readonly chipStatuses = ['Draft', 'Testing', 'Approved', 'Archived'] as const;
  protected readonly chipsOn = signal<readonly string[]>(['Testing']);

  protected toggleChip(status: string): void {
    this.chipsOn.update((on) => (on.includes(status) ? on.filter((each) => each !== status) : [...on, status]));
  }

  /** A value with a line break of its own, to show what `prose` keeps. */
  readonly topicCardNotes = 'Shot at noon, by the window.\nNo flash.';
  readonly buttonVariants: CpButtonVariant[] = ['primary', 'secondary', 'ghost', 'text', 'danger'];
  readonly buttonSizes: CpButtonSize[] = ['sm', 'md', 'lg'];
  readonly badgeTones: CpTone[] = ['neutral', 'success', 'pink', 'orange', 'purple', 'blue'];
  readonly quickActionTones: QuickActionTone[] = ['success', 'pink', 'orange', 'purple', 'blue'];
  readonly statusPillTones: CpStatusPillTone[] = ['neutral', 'progress', 'success', 'warning', 'error', 'stale'];

  /** Each toned meter travels with the pill that says the same thing in words, never colour alone. */
  readonly progressTones: {
    tone: CpProgressTone;
    pill: CpStatusPillTone;
    label: string;
    value: number;
    valueText: string;
  }[] = [
    { tone: 'success', pill: 'success', label: 'Plenty left', value: 36, valueText: '640 credits left' },
    { tone: 'warning', pill: 'warning', label: 'Nearly spent', value: 88, valueText: '120 credits left' },
    { tone: 'error', pill: 'error', label: 'Spent', value: 100, valueText: 'No credits left' },
  ];

  readonly dialogOpen = signal<'short' | 'long' | null>(null);
  readonly activationLog = signal<string[]>([]);

  onQuickActionActivated(tone: QuickActionTone): void {
    const entry = `${tone} quick action activated at ${new Date().toLocaleTimeString()}`;
    this.activationLog.update((log) => [entry, ...log].slice(0, 5));
  }

  closeDialog(): void {
    this.dialogOpen.set(null);
  }

  // --- Drawer ---
  private readonly confirm = inject(ConfirmService);
  private readonly drawerTitleAtOpen = 'Tomato soup, overhead';
  readonly drawerOpen = signal<'form' | 'long' | null>(null);
  readonly drawerTitle = signal(this.drawerTitleAtOpen);

  openDrawer(which: 'form' | 'long'): void {
    this.drawerTitle.set(this.drawerTitleAtOpen);
    this.drawerOpen.set(which);
  }

  onDrawerTitleInput(event: Event): void {
    this.drawerTitle.set((event.target as HTMLInputElement).value);
  }

  /** The drawer only asks. With nothing typed it closes; with something typed the creator is asked first. */
  async requestDrawerClose(): Promise<void> {
    if (this.drawerTitle() !== this.drawerTitleAtOpen) {
      const discard = await this.confirm.confirm({
        title: 'Discard changes?',
        message: 'The title you typed has not been saved.',
        confirmLabel: 'Discard changes',
        cancelLabel: 'Keep editing',
      });
      if (!discard) return;
    }
    this.closeDrawer();
  }

  closeDrawer(): void {
    this.drawerOpen.set(null);
  }

  // --- List shell ---
  readonly listShellState = signal<CpListShellState>('ready');
  readonly listShellStates: CpListShellState[] = ['loading', 'error', 'empty', 'ready'];

  // --- Tabs ---
  readonly tabs = [
    { id: 'ingredients', label: 'Ingredients' },
    { id: 'steps', label: 'Steps' },
    { id: 'notes', label: 'Notes', disabled: true },
  ];
  readonly selectedTabId = signal('ingredients');

  /** Enough tabs, with long enough labels, that one row cannot hold them at a typical width. */
  readonly manyTabs = [
    { id: 'tools', label: 'Tools' },
    { id: 'media', label: 'Media library' },
    { id: 'history', label: 'Version history' },
    { id: 'seo', label: 'SEO metadata' },
    { id: 'social', label: 'Social captions' },
    { id: 'newsletter', label: 'Newsletter copy' },
    { id: 'publishing', label: 'Publishing targets' },
    { id: 'analytics', label: 'Analytics' },
  ];
  readonly selectedManyTabId = signal('tools');

  readonly nestedTabs = [
    { id: 'scale', label: 'Scale' },
    { id: 'convert', label: 'Convert' },
    { id: 'yield', label: 'Yield & display' },
  ];
  readonly selectedNestedTabId = signal('scale');

  // --- Uploader ---
  readonly uploadItems = signal<CpUploadItem[]>([
    { id: '1', name: 'soup-hero.jpg', status: 'queued' },
    { id: '2', name: 'soup-detail.jpg', status: 'uploading', progress: 62 },
    { id: '3', name: 'soup-final.jpg', status: 'success' },
    { id: '4', name: 'soup-wide.jpg', status: 'error', errorMessage: 'File exceeds 10MB limit.' },
  ]);

  onUploaderFilesSelected(files: FileList): void {
    const next = Array.from(files).map((file, index) => ({
      id: `new-${Date.now()}-${index}`,
      name: file.name,
      status: 'queued' as const,
    }));
    this.uploadItems.update((items) => [...items, ...next]);
  }

  onUploaderRetry(id: string): void {
    this.uploadItems.update((items) =>
      items.map((item) => (item.id === id ? { ...item, status: 'uploading', progress: 0 } : item)),
    );
  }

  onUploaderCancel(id: string): void {
    this.uploadItems.update((items) => items.filter((item) => item.id !== id));
  }

  onUploaderRemove(id: string): void {
    this.uploadItems.update((items) => items.filter((item) => item.id !== id));
  }

  // --- Toast region ---
  /** The showcase's own anchor-nav sample, with one destination marked so the aria-current treatment is visible. */
  readonly anchorNavItems: CpAnchorNavItem[] = [
    { targetId: 'showcase-anchor-crust', label: 'For the crust', detail: '4 lines' },
    { targetId: 'showcase-anchor-filling', label: 'For the filling', detail: '1 line' },
    { targetId: 'showcase-anchor-glaze', label: 'For the glaze' },
  ];
  readonly activeAnchorNavId = signal<string | null>('showcase-anchor-filling');

  onAnchorNavActivated(item: CpAnchorNavItem): void {
    this.activeAnchorNavId.set(item.targetId);
  }

  /** A vocabulary a creator picks from, the shape R.15's unit picker and R.16's ingredient picker will pass. */
  readonly unitOptions: CpComboboxOption[] = [
    { id: 'g', label: 'Grams', detail: 'g · mass' },
    { id: 'kg', label: 'Kilograms', detail: 'kg · mass' },
    { id: 'cup', label: 'Cups', detail: 'c · volume' },
    { id: 'tbsp', label: 'Tablespoons', detail: 'tbsp · volume' },
    { id: 'tsp', label: 'Teaspoons', detail: 'tsp · volume' },
    { id: 'each', label: 'Each', detail: 'count' },
    { id: 'gill', label: 'Gills', detail: 'no longer offered', disabled: true },
  ];
  readonly unitText = signal('');
  readonly unit = signal<CpComboboxOption | null>(null);

  readonly tagOptions: CpComboboxOption[] = [
    { id: 'weeknight', label: 'Weeknight', detail: '18 recipes' },
    { id: 'make-ahead', label: 'Make ahead', detail: '7 recipes' },
    { id: 'one-pan', label: 'One pan', detail: '4 recipes' },
  ];
  readonly tagText = signal('');
  readonly tag = signal<CpComboboxOption | null>(null);

  readonly voiceChoices: readonly CpChoiceOption[] = [
    { value: 'warm', label: 'Warm friend', hint: 'Like chatting in the kitchen', example: '“Trust me on this one.”' },
    { value: 'teacher', label: 'Calm teacher', hint: 'Steady, one step at a time', example: '“Brown the sausage until no pink remains.”' },
    { value: 'pro', label: 'Expert pro', hint: 'Precise, for cooks who want the why', example: '“Brown it thoroughly to build a deeper base.”' },
  ];

  readonly toneChoices: readonly CpChoiceOption[] = [
    { value: 'cozy', label: 'Cozy and comforting' },
    { value: 'upbeat', label: 'Upbeat and energetic' },
    { value: 'calm', label: 'Calm and reassuring' },
    { value: 'confident', label: 'Confident and direct' },
  ];

  readonly channelChoices: readonly CpChoiceOption[] = [
    { value: 'blog', label: 'Blog' },
    { value: 'instagram', label: 'Instagram' },
    { value: 'pinterest', label: 'Pinterest' },
    { value: 'newsletter', label: 'Newsletter' },
  ];

  readonly showcaseVoice = signal<readonly string[]>(['warm']);
  readonly showcaseTones = signal<readonly string[]>(['cozy', 'calm']);
  readonly showcaseChannels = signal<readonly string[]>(['blog']);

  readonly showcaseOptional = signal(false);
  readonly showcaseAbbreviate = signal(true);

  readonly toasts = signal<CpToast[]>([]);

  readonly brandGuide: BrandGuideChoice = {
    guideId: 'g1',
    name: 'Warm kitchen voice',
    versionNumber: 3,
    isStale: false,
    approvedAt: '2026-03-14T10:00:00Z',
    appliedSections: ['Tone', 'Vocabulary', 'Point of view'],
  };
  readonly brandGuideStale: BrandGuideChoice = {
    ...this.brandGuide,
    isStale: true,
    staleReason: 'A source document was replaced after this version was approved.',
  };
  readonly brandGuideChoices: readonly BrandGuideChoice[] = [
    { guideId: 'g2', name: 'Weeknight voice', versionNumber: 2, isStale: false },
    { guideId: 'g3', name: 'Holiday baking', versionNumber: 1, isStale: true },
  ];
  readonly brandRules: readonly BrandGuideChannelRule[] = [
    { channel: 'Blog', summary: 'Conversational and first person, with a story before the recipe.' },
    { channel: 'Instagram', summary: 'Short and playful, no more than five hashtags.' },
  ];
  readonly brandSelection = signal<BrandGuideSelection>({ kind: 'default' });
  readonly brandStaleAck = signal(false);

  readonly visualLines: readonly BrandVisualStyleLine[] = [
    { label: 'Visual identity', summary: 'Warm, honest, never staged.' },
    { label: 'Photography direction', summary: 'Soft window light from the left, shallow depth of field.' },
  ];
  readonly visualReferences: readonly BrandVisualReference[] = [
    { documentId: 'ref-spring', title: 'Spring moodboard', usable: true },
    { documentId: 'ref-table', title: 'Table settings I love', usable: true },
    { documentId: 'ref-scan', title: 'Scanned inspiration board', usable: false },
  ];
  readonly visualSelection = signal<BrandGuideSelection>({ kind: 'default' });
  readonly visualStaleAck = signal(false);
  readonly visualReferenceIds = signal<readonly string[]>(['ref-spring']);
  private toastCounter = 0;

  pushToast(severity: CpToastSeverity): void {
    const messages: Record<CpToastSeverity, string> = {
      info: 'Draft saved.',
      success: 'Recipe published.',
      warning: 'This recipe is missing a yield.',
      error: 'Publishing failed — check your connection.',
    };
    this.toasts.update((toasts) => [...toasts, { id: `toast-${++this.toastCounter}`, message: messages[severity], severity }]);
  }

  onToastDismissed(id: string): void {
    this.toasts.update((toasts) => toasts.filter((toast) => toast.id !== id));
  }
}
