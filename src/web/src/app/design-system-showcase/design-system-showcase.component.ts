import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import {
  CpBadgeComponent,
  CpButtonComponent,
  CpButtonSize,
  CpButtonVariant,
  CpCardComponent,
  CpDiffLegendComponent,
  CpDialogComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpListShellComponent,
  CpListShellState,
  CpProgressComponent,
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
    CpProgressComponent,
    CpQuickActionComponent,
    CpStatusPillComponent,
    CpListShellComponent,
    CpTabsComponent,
    CpTabPanelComponent,
    CpToolbarComponent,
    CpEmptyStateComponent,
    CpUploaderComponent,
    CpDiffLegendComponent,
    CpToastRegionComponent,
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
  readonly toasts = signal<CpToast[]>([]);
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
