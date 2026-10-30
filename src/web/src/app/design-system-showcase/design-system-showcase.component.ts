import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';

import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpBackLinkComponent,
  CpBadgeComponent,
  CpButtonComponent,
  CpButtonSize,
  CpButtonVariant,
  CpCardComponent,
  CpBoardColumnComponent,
  CpBoardComponent,
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
  CpLightboxComponent,
  CpEmptyStateComponent,
  CpFactComponent,
  CpFactsComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpGeneratedComponent,
  CpGeneratedLabelComponent,
  CpIllustrationComponent,
  CpIllustrationName,
  CP_ILLUSTRATION_NAMES,
  CpListShellComponent,
  CpListShellLoadingLayout,
  CpListShellState,
  CpMediaCardComponent,
  CpSkeletonComponent,
  CpSpinnerComponent,
  CpStatTileComponent,
  CpNoticeComponent,
  CpOptionCardComponent,
  CpOptionFact,
  CpPageHeadComponent,
  CpProgressComponent,
  CpLoadingBarComponent,
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
  CpValidationSummaryComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../core/confirm.service';
import { FormValidator } from '../core/forms/form-validator';
import { range, required, warn } from '../core/forms/form-rules';
import { BrandGuideChannelRule, BrandGuideChoice, BrandGuideSelection } from '../models/brand-guide-control.models';
import { BrandGuideControlComponent } from '../shared/brand-guide-control/brand-guide-control.component';
import { BrandVisualStyleControlComponent } from '../shared/brand-visual-style-control/brand-visual-style-control.component';
import { BrandVisualReference, BrandVisualStyleLine } from '../models/brand-visual-style-control.models';
import { ShellNavIcon, ShellNavIconComponent } from '../shared/nav-icon/shell-nav-icon.component';

type QuickActionTone = Exclude<CpTone, 'neutral'>;

@Component({
  selector: 'cp-design-system-showcase',
  standalone: true,
  imports: [
    CpPageHeadComponent,
    CpBackLinkComponent,
    CpBoardColumnComponent,
    CpBoardComponent,
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
    CpLightboxComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    CpGeneratedComponent,
    CpGeneratedLabelComponent,
    CpProgressComponent,
    CpLoadingBarComponent,
    CpQuickActionComponent,
    CpStatusPillComponent,
    CpListShellComponent,
    CpSkeletonComponent,
    CpSpinnerComponent,
    CpStatTileComponent,
    CpIllustrationComponent,
    ShellNavIconComponent,
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
    CpOptionCardComponent,
    CpComboboxComponent,
    CpDiffLegendComponent,
    CpToastRegionComponent,
    BrandGuideControlComponent,
    BrandVisualStyleControlComponent,
    CpStepperComponent,
    CpValidationSummaryComponent,
  ],
  templateUrl: './design-system-showcase.component.html',
  styleUrl: './design-system-showcase.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DesignSystemShowcaseComponent {
  /** The option-card example: three concepts, one chosen at a time, the way a studio offers them. */
  protected readonly concepts: readonly { id: string; heading: string; summary: string; facts: readonly CpOptionFact[] }[] = [
    {
      id: 'miso',
      heading: 'Smoky miso corn',
      summary: 'Charred corn brushed with miso butter, finished with lime and chives.',
      facts: [
        { label: 'Time', value: '30 min' },
        { label: 'Skill', value: 'Weeknight' },
        { label: 'Serves', value: '4' },
      ],
    },
    {
      id: 'salad',
      heading: 'Charred corn salad',
      summary: 'Corn off the cob with tomatoes, feta and a sharp dressing.',
      facts: [
        { label: 'Time', value: '20 min' },
        { label: 'Skill', value: 'Beginner' },
        { label: 'Serves', value: '6' },
        { label: 'Dietary notes', value: 'Vegetarian as written — descriptive only, not a guarantee.' },
      ],
    },
    {
      id: 'chowder',
      heading: 'Sweet corn chowder',
      summary: 'A slow pot of corn, potato and smoked paprika.',
      facts: [
        { label: 'Time', value: '1 hr 10 min' },
        { label: 'Skill', value: 'Comfortable' },
      ],
    },
  ];
  protected readonly chosenConcept = signal('miso');

  /**
   * The validation example: the same engine a real form uses. A blank title is an error that shows once the
   * creator leaves the field or tries to save; a very large batch is a warning that shows as they type.
   */
  protected readonly validationDraft = signal({ title: '', servings: 4 });
  protected readonly validation = new FormValidator(
    {
      title: [required('Give the recipe a name.')],
      servings: [
        range({ min: 1 }, 'Servings must be at least 1.'),
        warn(range({ max: 40 }, 'That is a lot of servings — is it a batch?')),
      ],
    },
    this.validationDraft,
  );
  protected readonly validationLabelOf = (path: string): string => (path === 'title' ? 'Title' : path === 'servings' ? 'Servings' : '');
  private readonly validationSummary = viewChild(CpValidationSummaryComponent);

  protected setValidationTitle(title: string): void {
    this.validationDraft.update((draft) => ({ ...draft, title }));
  }

  protected setValidationServings(value: string): void {
    this.validationDraft.update((draft) => ({ ...draft, servings: value === '' ? 0 : Number(value) }));
  }

  protected valueOf(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected trySaveValidationDemo(): void {
    this.validation.attempt();
    this.validationSummary()?.focus();
  }

  /** Takes the creator to the field an issue is about — the part a summary leaves to its form. */
  protected focusValidationField(path: string): void {
    document.getElementById(`showcase-validation-${path}`)?.focus();
  }

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

  /** The busy button's pretend save: long enough to see the spinner and the label change, and hear `aria-busy`. */
  readonly busyDemo = signal(false);

  pretendToSave(): void {
    if (this.busyDemo()) return;
    this.busyDemo.set(true);
    setTimeout(() => this.busyDemo.set(false), 2000);
  }

  /** The loading bar's example: a wait the reader starts and ends, so the 200ms before it shows can be felt. */
  readonly loadingBarActive = signal(false);

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
  readonly lightboxOpen = signal<'plain' | 'full' | null>(null);
  /**
   * A stand-in picture, inline so the showcase asks the network for nothing. Its greys are the picture's own
   * pixels, not theme colours: a photograph does not change with the theme either.
   */
  readonly samplePicture =
    'data:image/svg+xml,' +
    encodeURIComponent(
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 900"><rect width="1200" height="900" fill="#8a8f8c"/>' +
        '<circle cx="600" cy="450" r="260" fill="#c9cdc9"/><circle cx="600" cy="450" r="170" fill="#a3a8a4"/></svg>',
    );

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

  // --- Entrance motion ---
  /** The run on screen, as a list of one, so the three parts can be taken away and built again (AU.3.4). */
  readonly entranceRuns = signal<readonly number[]>([0]);
  private entranceRun = 0;

  replayEntrance(): void {
    // Emptied in this change detection and filled in the next, so the parts are genuinely built again and
    // `track` never sees one key re-created, which is what NG0956 warns about.
    this.entranceRuns.set([]);
    queueMicrotask(() => this.entranceRuns.set([++this.entranceRun]));
  }

  // --- List shell ---
  readonly listShellState = signal<CpListShellState>('ready');
  readonly listShellStates: CpListShellState[] = ['loading', 'error', 'empty', 'ready'];
  readonly listShellLayout = signal<CpListShellLoadingLayout>('rows');
  readonly listShellLayouts: CpListShellLoadingLayout[] = ['rows', 'cards'];
  /** A board's loading layout: its columns by name, with skeleton cards in uneven numbers (AU.6.1). */
  readonly boardSkeleton = [
    { heading: 'To do', cards: [1, 2, 3] },
    { heading: 'In process', cards: [1, 2] },
    { heading: 'Ready for review', cards: [1] },
    { heading: 'Approved', cards: [1, 2] },
  ];

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

  /** The drawn set (AU.2.4), every one of it. */
  readonly illustrationNames: readonly CpIllustrationName[] = CP_ILLUSTRATION_NAMES;

  /** One head per tool tone (AU.2.5, D3), each with its section's nav icon. */
  readonly tonedHeads: readonly { tone: 'purple' | 'pink' | 'blue' | 'orange' | null; icon: ShellNavIcon; heading: string; lede: string; action: string }[] = [
    { tone: null, icon: 'recipes', heading: 'Recipes', lede: 'Every recipe in this workspace. Search and filter to find one.', action: 'New recipe' },
    { tone: 'purple', icon: 'ai', heading: 'AI Recipe Studio', lede: 'Describe what you are after and get a handful of distinct recipe pitches back.', action: 'Get concepts' },
    { tone: 'pink', icon: 'image', heading: 'Image Studio', lede: 'Describe a shot, get the words for it, and turn them into pictures.', action: 'Make pictures' },
    { tone: 'blue', icon: 'social', heading: 'Social Studio', lede: 'Write posts for something you already have.', action: 'Write posts' },
    { tone: 'orange', icon: 'workflows', heading: 'Long-form', lede: 'From what a piece is about to an article you have accepted, one step at a time.', action: 'Continue' },
  ];

  /** Every tool tone (AU.1, D1), and no tone, which is the primary. */
  readonly tones: readonly { name: 'purple' | 'pink' | 'blue' | 'orange' | null; tool: string }[] = [
    { name: null, tool: 'Recipes' },
    { name: 'purple', tool: 'AI Recipe Studio' },
    { name: 'pink', tool: 'Image Studio' },
    { name: 'blue', tool: 'Social Studio' },
    { name: 'orange', tool: 'Long-form' },
  ];

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
