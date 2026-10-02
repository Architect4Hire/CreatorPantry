import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import {
  CpBadgeComponent,
  CpButtonComponent,
  CpButtonSize,
  CpButtonVariant,
  CpCardComponent,
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpCheckboxComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpDiffLegendComponent,
  CpDialogComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpListShellComponent,
  CpListShellState,
  CpProgressComponent,
  CpProgressTone,
  CpQuickActionComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpTabPanelComponent,
  CpTabsComponent,
  CpToast,
  CpToastRegionComponent,
  CpToastSeverity,
  CpToolbarComponent,
  CpTone,
  CpUploadItem,
  CpUploaderComponent,
} from '@creator-pantry/ui';

import { BrandGuideChannelRule, BrandGuideChoice, BrandGuideSelection } from '../models/brand-guide-control.models';
import { BrandGuideControlComponent } from '../shared/brand-guide-control/brand-guide-control.component';

type QuickActionTone = Exclude<CpTone, 'neutral'>;

@Component({
  selector: 'cp-design-system-showcase',
  standalone: true,
  imports: [
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpDialogComponent,
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
    CpComboboxComponent,
    CpDiffLegendComponent,
    CpToastRegionComponent,
    BrandGuideControlComponent,
  ],
  templateUrl: './design-system-showcase.component.html',
  styleUrl: './design-system-showcase.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DesignSystemShowcaseComponent {
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
