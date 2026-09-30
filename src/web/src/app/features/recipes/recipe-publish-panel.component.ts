import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import {
  DEFAULT_EXPORT_CHOICES,
  EXPORT_PAGE_SIZE_OPTIONS,
  EXPORT_TEMPLATE_OPTIONS,
  EXPORT_UNITS_OPTIONS,
  NOT_EXPORTABLE_REASON_NOT_APPROVED,
  RecipeExportAcceptedCopy,
  RecipeExportChoices,
  RecipeExportPageSize,
  RecipeExportSummary,
  RecipeExportTemplate,
  RecipeExportUnits,
} from '../../models/recipe-export.models';
import { RecipeExportFormat, RecipeExportService } from '../../services/recipe-export.service';

type PublishState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly summary: RecipeExportSummary }
  | { readonly status: 'not_found' }
  | { readonly status: 'error' };

interface CopyRow {
  readonly key: 'editorial' | 'seo';
  readonly label: string;
  readonly copy: RecipeExportAcceptedCopy | null;
  /** Where this copy would go, said in the creator's terms. */
  readonly usedBy: string;
}

interface DownloadRow {
  readonly format: RecipeExportFormat;
  readonly title: string;
  readonly description: string;
  readonly actionLabel: string;
  /** JSON-LD is text a browser shows; Markdown and PDF are served as downloads, so only JSON-LD previews. */
  readonly previewable: boolean;
  readonly href: string | null;
}

const DOWNLOADS: readonly Omit<DownloadRow, 'href'>[] = [
  {
    format: 'json-ld',
    title: 'Structured data (JSON-LD)',
    description: 'schema.org Recipe markup for the page this recipe is published on.',
    actionLabel: 'Download JSON-LD',
    previewable: true,
  },
  {
    format: 'markdown',
    title: 'Markdown',
    description: 'A plain-text copy with your own wording, for a blog editor or an archive.',
    actionLabel: 'Download Markdown',
    previewable: false,
  },
  {
    format: 'pdf',
    title: 'PDF',
    description: 'A printable recipe card with real, selectable text.',
    actionLabel: 'Download PDF',
    previewable: false,
  },
];

/**
 * The Publish panel (RCPUB-002/003/004): what an export of this recipe would be built from, and the downloads.
 *
 * **Nothing here produces or judges an export.** Whether the version may be exported, which accepted copy is
 * current, and every document come from the server. This component shows the summary and builds links; the
 * browser follows a link and the server answers it, so no document passes through Angular and no storage or
 * provider address exists to show.
 *
 * **Links are pinned to the version the summary described.** A recipe edited elsewhere changes what the next
 * summary says, not what a link already on screen downloads.
 *
 * **Copy that is not current is named as left out, not hidden.** The server omits it from the export and
 * says so in a header; this panel says the same thing before the creator clicks.
 */
@Component({
  selector: 'cp-recipe-publish-panel',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-publish-panel.component.html',
  styleUrl: './recipe-publish-panel.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipePublishPanelComponent {
  private readonly exportService = inject(RecipeExportService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The recipe's current version number as the host knows it. A change means the recipe was saved or restored,
   * so the summary is read again; it carries no meaning beyond that.
   */
  readonly versionHint = input<number | null>(null);

  readonly templateOptions = EXPORT_TEMPLATE_OPTIONS;
  readonly unitsOptions = EXPORT_UNITS_OPTIONS;
  readonly pageSizeOptions = EXPORT_PAGE_SIZE_OPTIONS;

  readonly state = signal<PublishState>({ status: 'loading' });
  readonly template = signal<RecipeExportTemplate>(DEFAULT_EXPORT_CHOICES.template);
  readonly units = signal<RecipeExportUnits>(DEFAULT_EXPORT_CHOICES.units);
  readonly pageSize = signal<RecipeExportPageSize>(DEFAULT_EXPORT_CHOICES.pageSize);

  /** Announced politely when a read finishes, so a reload is not silent to a screen reader. */
  readonly announcement = signal('');

  private loadToken = 0;

  readonly summary = computed(() => {
    const state = this.state();
    return state.status === 'ready' ? state.summary : null;
  });

  readonly choices = computed<RecipeExportChoices>(() => ({
    template: this.template(),
    units: this.units(),
    pageSize: this.pageSize(),
  }));

  readonly copyRows = computed<readonly CopyRow[]>(() => {
    const summary = this.summary();
    if (!summary) return [];

    return [
      { key: 'editorial', label: 'Editorial copy', copy: summary.editorial, usedBy: 'Markdown and PDF, standard layout' },
      { key: 'seo', label: 'SEO copy', copy: summary.seo, usedBy: 'structured data' },
    ];
  });

  readonly downloads = computed<readonly DownloadRow[]>(() => {
    const summary = this.summary();
    const slug = this.workspaceSlug();
    const recipeId = this.recipeId();
    const choices = this.choices();

    return DOWNLOADS.map((row) => ({
      ...row,
      href: summary?.exportable
        ? this.exportService.downloadUrl(slug, recipeId, row.format, summary.versionNumber, choices)
        : null,
    }));
  });

  /** Links are built from a gateway address the app may not have yet; in that case the panel says so. */
  readonly linksUnavailable = computed(() => {
    const summary = this.summary();
    return summary?.exportable === true && this.downloads().every((row) => row.href === null);
  });

  readonly notExportableMessage = computed(() => {
    const summary = this.summary();
    if (!summary || summary.exportable) return '';

    return summary.notExportableReason === NOT_EXPORTABLE_REASON_NOT_APPROVED
      ? `Version ${summary.versionNumber} is not approved or marked ready, so it cannot be exported yet.`
      : `Version ${summary.versionNumber} cannot be exported right now.`;
  });

  constructor() {
    // Reads on first show and whenever the recipe, the workspace or the saved version changes.
    effect(() => {
      this.workspaceSlug();
      this.recipeId();
      this.versionHint();
      untracked(() => void this.load());
    });
  }

  async load(): Promise<void> {
    const token = ++this.loadToken;
    this.state.set({ status: 'loading' });

    const outcome = await this.exportService.getSummary(this.workspaceSlug(), this.recipeId());
    // A slower earlier read must not overwrite a later one.
    if (token !== this.loadToken) return;

    switch (outcome.status) {
      case 'found':
        this.state.set({ status: 'ready', summary: outcome.summary });
        this.announcement.set(`Export options loaded for version ${outcome.summary.versionNumber}.`);
        break;
      case 'not_found':
        this.state.set({ status: 'not_found' });
        break;
      case 'unavailable':
        this.state.set({ status: 'error' });
        break;
    }
  }

  copyTone(copy: RecipeExportAcceptedCopy | null): CpStatusPillTone {
    if (copy === null) return 'neutral';
    return copy.isCurrent ? 'success' : 'stale';
  }

  copyStatusLabel(copy: RecipeExportAcceptedCopy | null): string {
    if (copy === null) return 'Nothing accepted';
    return copy.isCurrent ? 'Current' : 'Needs review';
  }

  copyDetail(row: CopyRow): string {
    if (row.copy === null) {
      return `Nothing is accepted yet, so ${row.usedBy} will carry none.`;
    }

    return row.copy.isCurrent
      ? `Revision ${row.copy.revisionNumber} is included in ${row.usedBy}.`
      : `Revision ${row.copy.revisionNumber} was accepted for an earlier version of the recipe, or is marked for review. It is left out of ${row.usedBy} until you review it.`;
  }
}
