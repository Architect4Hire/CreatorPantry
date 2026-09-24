import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import {
  CpButtonComponent,
  CpEmptyStateComponent,
  CpListShellComponent,
  CpListShellState,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { WorkspaceRole } from '../../models/auth.models';
import { RecipeVersionComparison, RecipeVersionHistoryEntry } from '../../models/recipe-version.models';
import { RecipeVersionSource } from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeDuplicateComponent, RecipeDuplicated } from './recipe-duplicate.component';
import { RecipeVersionComparisonComponent } from './recipe-version-comparison.component';
import { RecipeRestoreComponent, RecipeVersionRestored } from './recipe-restore.component';

type HistoryLoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

type ComparisonState =
  | { readonly status: 'idle' }
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly comparison: RecipeVersionComparison }
  /** One of the chosen numbers names no version of this recipe — the history moved on under the picker. */
  | { readonly status: 'stale' }
  | { readonly status: 'unavailable' };

/** How a version's provenance reads, when it is worth saying at all. */
interface ProvenanceLabel {
  readonly text: string;
  readonly icon: string;
}

/**
 * Provenance worth a pill.
 *
 * `CreatorEdit` is deliberately absent: it is what almost every version is, and a badge on every row would
 * be noise that makes the rows that differ harder to spot. AI-UI-003 asks for generated content to be
 * *identified*, which is what the others do — an accepted proposal in particular stays identifiable after
 * the fact, which is the traceability ai.md records the field for.
 */
const PROVENANCE: Partial<Record<RecipeVersionSource, ProvenanceLabel>> = {
  AiProposalAccepted: { text: 'AI-assisted', icon: '✦' },
  Restore: { text: 'Restored', icon: '↺' },
  Duplicate: { text: 'Copied', icon: '⧉' },
  Import: { text: 'Imported', icon: '↓' },
};

/** Makes each panel's radio groups unique, so two panels on one page cannot share a selection. */
let nextInstance = 0;

/** The roles `AuthorizationPolicies.WorkspaceEditor` admits — the bar a restore has to clear. */
const RESTORE_ROLES: readonly WorkspaceRole[] = ['Editor', 'Owner'];

/** `AuthorizationPolicies.WorkspaceContributor` — the lower bar a duplicate clears, the same as a create. */
const DUPLICATE_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

/**
 * A recipe's version history, and the comparison the creator builds from it.
 *
 * Read-only throughout (REC-007). The list shows what each version says about itself — number, when, who,
 * provenance, readiness, the creator's reason — and is where two versions are chosen; the comparison is
 * calculated by the server and rendered by `cp-recipe-version-comparison`, which derives nothing of its own,
 * because REC-008 wants one diff a creator can trust rather than a client's second opinion about it.
 *
 * Two commands are reachable from here, and they are reachable rather than performed: a row's buttons open
 * `cp-recipe-restore` (REC-009) or `cp-recipe-duplicate` (REC-005), each owning its own confirmation, request
 * and failures. Archive is not here. The reading of the history — what each version says, and the comparison
 * — is untouched by either, and each button is shown only to a role that could use it: restoring takes the
 * recipe somewhere and carries the Editor bar, while copying takes nothing from anyone and carries the
 * Contributor bar a create does.
 *
 * Versions are chosen by number rather than by id, matching the comparison route: a number is unique only
 * within its recipe, so one belonging to another recipe matches nothing rather than being refused by a check.
 */
@Component({
  selector: 'cp-recipe-history',
  standalone: true,
  imports: [
    DatePipe,
    CpButtonComponent,
    CpEmptyStateComponent,
    CpListShellComponent,
    CpStatusPillComponent,
    RecipeDuplicateComponent,
    RecipeRestoreComponent,
    RecipeVersionComparisonComponent,
  ],
  templateUrl: './recipe-history.component.html',
  styleUrl: './recipe-history.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeHistoryComponent implements OnInit {
  private readonly recipeService = inject(RecipeService);
  private readonly membershipService = inject(WorkspaceMembershipService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The recipe's `concurrencyToken` as the editor last read it, passed straight through to a restore.
   *
   * The panel never reads or refreshes it: the editor owns the recipe, and a second read here could hand a
   * restore a token that disagrees with the form the creator is looking at.
   */
  readonly concurrencyToken = input<string | null>(null);

  /** Whether the editor holds unsaved edits, which a restore would replace. */
  readonly editorIsDirty = input(false);

  /**
   * The source recipe's title, shown by the duplicate dialog so the copy's empty title box has something to
   * name. Empty until the editor has loaded, which is also before the trigger can be reached.
   */
  readonly recipeTitle = input('');

  /** A completed restore, for the host to apply to the recipe it owns. */
  readonly restored = output<RecipeVersionRestored>();

  /** A copy that was made, for the host to navigate to. The copy is a recipe of its own, not a derivative. */
  readonly duplicated = output<RecipeDuplicated>();

  /** A restore refused against stale state: the host re-reads the recipe, and this reloads the history. */
  readonly reloadRequested = output<void>();

  private readonly loadStateSignal = signal<HistoryLoadState>({ status: 'loading' });
  readonly loadState = this.loadStateSignal.asReadonly();

  private readonly versionsSignal = signal<readonly RecipeVersionHistoryEntry[]>([]);
  readonly versions = this.versionsSignal.asReadonly();

  /**
   * The cursor for the page after the one loaded, or null at the end of the history.
   *
   * Kept rather than discarded: a recipe with more versions than one page would otherwise hide its older
   * ones with no cue that they exist, which is a degraded state rendered as a complete one (frontend.md).
   */
  private readonly nextCursorSignal = signal<string | null>(null);
  readonly hasOlderVersions = computed(() => this.nextCursorSignal() !== null);

  private readonly loadingOlderSignal = signal(false);
  readonly loadingOlder = this.loadingOlderSignal.asReadonly();

  private readonly comparisonStateSignal = signal<ComparisonState>({ status: 'idle' });
  readonly comparisonState = this.comparisonStateSignal.asReadonly();

  /** The button the open dialog was raised from, so focus can be handed back to it. */
  private commandTrigger: HTMLElement | null = null;

  /** The version a restore is being composed for, or null when no dialog is open. */
  private readonly restoreTargetSignal = signal<RecipeVersionHistoryEntry | null>(null);
  readonly restoreTarget = this.restoreTargetSignal.asReadonly();

  /** The version a copy is being composed from, or null when no dialog is open. */
  private readonly duplicateTargetSignal = signal<RecipeVersionHistoryEntry | null>(null);
  readonly duplicateTarget = this.duplicateTargetSignal.asReadonly();

  /**
   * What the recipe currently says, as a version number.
   *
   * Zero before the history has loaded, which no row can equal — the picker is not rendered then either, so
   * nothing reads it in that state.
   */
  readonly newestVersionNumber = computed(() => this.versions()[0]?.versionNumber ?? 0);

  /**
   * Whether this member could restore a version at all.
   *
   * Restoring requires the Editor role (`AuthorizationPolicies.WorkspaceEditor`), so a Viewer or Contributor
   * is shown no button rather than one that can only answer 403. The server is still the authority — the
   * dialog reports a refusal if this is ever wrong — but offering an action nobody can take is its own defect
   * in a panel whose whole job is to be read.
   */
  readonly canRestore = computed(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && RESTORE_ROLES.includes(role);
  });

  /**
   * Whether this member could copy a version.
   *
   * A lower bar than restoring, matching the route: duplicating requires only Contributor, the same as
   * creating a recipe, because a copy takes nothing away from anyone.
   */
  readonly canDuplicate = computed(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && DUPLICATE_ROLES.includes(role);
  });

  /** Which two versions the creator has chosen, as numbers. Null until the history has loaded. */
  readonly fromVersion = signal<number | null>(null);
  readonly toVersion = signal<number | null>(null);

  /**
   * Per-instance radio group names.
   *
   * Two radio groups rather than one: "from" and "to" are independent choices, and a single group could only
   * ever hold one of them. Sharing a `name` across two mounted panels would make their selections fight, and
   * a name is what makes arrow keys walk one group rather than every radio on the page.
   */
  private readonly instance = nextInstance++;
  readonly fromGroup = `cp-recipe-history-from-${this.instance}`;
  readonly toGroup = `cp-recipe-history-to-${this.instance}`;

  /** The frame's state, mapped from the load state so the shell owns loading, error and empty rendering. */
  readonly listState = computed<CpListShellState>(() => {
    switch (this.loadState().status) {
      case 'loading':
        return 'loading';
      case 'ready':
        return this.versions().length === 0 ? 'empty' : 'ready';
      default:
        return 'error';
    }
  });

  readonly errorMessage = computed(() =>
    this.loadState().status === 'not_found'
      ? 'This recipe could not be found.'
      : "Couldn't load the version history. Check your connection and try again.",
  );

  /**
   * Whether a comparison can be asked for at all.
   *
   * A recipe with one version has a history worth reading and nothing to compare it against, so the list
   * renders and the picker does not — which is why this is separate from the list having rows.
   */
  readonly canChoose = computed(() => this.versions().length >= 2);

  readonly canCompare = computed(() => this.fromVersion() !== null && this.toVersion() !== null);

  /**
   * Whether the two selections are the same version. Not an error — the server accepts it and answers "no
   * changes" — but worth saying before the request, because the answer is predictable.
   */
  readonly comparingWithItself = computed(() => this.canCompare() && this.fromVersion() === this.toVersion());

  readonly comparing = computed(() => this.comparisonState().status === 'loading');

  /**
   * One line of status, read from a live region that is always in the DOM.
   *
   * An `aria-live` element inserted already populated is not reliably announced, so a region created inside
   * the `@switch` alongside its own text would leave a creator who pressed Compare hearing nothing when the
   * answer landed.
   */
  readonly statusMessage = computed(() => {
    if (this.loadState().status === 'loading') return 'Loading version history…';

    switch (this.comparisonState().status) {
      case 'loading':
        return 'Comparing…';
      case 'ready':
        return 'Comparison ready.';
      case 'idle': {
        if (this.loadState().status !== 'ready' || !this.canChoose()) return '';

        const from = this.fromVersion();
        const to = this.toVersion();

        // The selection itself, so a screen-reader user who has just moved a radio hears which pair they
        // are about to compare rather than only that the control moved.
        return from !== null && to !== null
          ? `Comparing version ${from} with version ${to}.`
          : 'Choose two versions and select Compare.';
      }
      default:
        return '';
    }
  });

  /**
   * Loaded here rather than in the constructor: the required inputs this read needs are not bound yet when
   * the constructor runs. The panel is mounted lazily by its tab, so this fires when the tab is first opened
   * rather than when the editor loads.
   */
  ngOnInit(): void {
    void this.load();

    // Shared with the workspace switcher and already loaded in almost every case; this only covers a creator
    // who opened the history tab before it settled. A failure leaves `canRestore()` false, which hides the
    // button rather than offering one whose permission is unknown.
    void this.membershipService.ensureLoaded();
  }

  async load(): Promise<void> {
    this.loadStateSignal.set({ status: 'loading' });

    const outcome = await this.recipeService.getVersionHistory(this.workspaceSlug(), this.recipeId());

    if (outcome.status !== 'found') {
      // A stale cursor cannot occur on a first page, so the two remaining unreadable outcomes are reported
      // the same way: the remedy for both is to try again.
      this.loadStateSignal.set(outcome.status === 'not_found' ? { status: 'not_found' } : { status: 'unavailable' });
      return;
    }

    const items = outcome.page.items;
    this.versionsSignal.set(items);
    this.nextCursorSignal.set(outcome.page.nextCursor);
    this.loadStateSignal.set({ status: 'ready' });

    // A reload answers a different question from the one any rendered comparison answered, and the versions
    // it named may no longer exist. Clearing it is what keeps the panel from outliving its question.
    this.comparisonStateSignal.set({ status: 'idle' });

    // Newest against the one before it: the comparison a creator opening this tab almost always wants, and
    // the only pair that can be preselected without guessing. A recipe with one version preselects nothing.
    if (items.length >= 2) {
      this.fromVersion.set(items[1].versionNumber);
      this.toVersion.set(items[0].versionNumber);
    } else {
      this.fromVersion.set(null);
      this.toVersion.set(null);
    }
  }

  /**
   * Appends the next page of older versions.
   *
   * Separate from {@link load} rather than folded into it: reloading answers "what does the history look
   * like now" and resets the picker, while this one only widens the choice a creator already has.
   */
  async loadOlder(): Promise<void> {
    const cursor = this.nextCursorSignal();
    if (cursor === null || this.loadingOlderSignal()) return;

    this.loadingOlderSignal.set(true);

    try {
      const outcome = await this.recipeService.getVersionHistory(this.workspaceSlug(), this.recipeId(), cursor);

      if (outcome.status !== 'found') {
        // A cursor the server will not accept means the history moved on beneath it. Dropping the cursor
        // stops the offer rather than leaving a button that can only fail; a full reload is still available.
        this.nextCursorSignal.set(null);
        return;
      }

      this.versionsSignal.update((existing) => [...existing, ...outcome.page.items]);
      this.nextCursorSignal.set(outcome.page.nextCursor);
    } finally {
      this.loadingOlderSignal.set(false);
    }
  }

  async compare(): Promise<void> {
    const from = this.fromVersion();
    const to = this.toVersion();
    if (from === null || to === null) return;

    // A second request while one is in flight can land first and be overwritten by the slower earlier one,
    // leaving a comparison on screen that does not answer the question the picker is showing.
    if (this.comparisonStateSignal().status === 'loading') return;

    this.comparisonStateSignal.set({ status: 'loading' });

    const outcome = await this.recipeService.compareVersions(this.workspaceSlug(), this.recipeId(), from, to);

    switch (outcome.status) {
      case 'found':
        this.comparisonStateSignal.set({ status: 'ready', comparison: outcome.comparison });
        break;
      // Both mean the picker is describing versions the server does not have — the recipe changed underneath
      // it, or a number was never valid. Reloading the list is the remedy either way, so they read alike.
      case 'version_not_found':
      case 'invalid_request':
        this.comparisonStateSignal.set({ status: 'stale' });
        break;
      default:
        this.comparisonStateSignal.set({ status: 'unavailable' });
        break;
    }
  }

  /**
   * Opens the restore dialog for one version. Opening writes nothing; the dialog owns the command.
   *
   * The button that opened it is kept so focus can go back where it came from when the dialog closes with
   * nothing done — `CpDialogComponent` moves focus in and does not return it.
   */
  openRestore(version: RecipeVersionHistoryEntry, trigger: EventTarget | null): void {
    this.commandTrigger = trigger instanceof HTMLElement ? trigger : null;
    this.restoreTargetSignal.set(version);
  }

  /** Closed with nothing restored, so the creator is put back on the control they opened it from. */
  closeRestore(): void {
    this.restoreTargetSignal.set(null);
    this.returnFocus();
  }

  /**
   * Hands focus back to the button a dialog was opened from.
   *
   * `CpDialogComponent` moves focus in and does not return it, so without this a cancelled command leaves a
   * keyboard user at the top of the document.
   */
  private returnFocus(): void {
    this.commandTrigger?.focus();
    this.commandTrigger = null;
  }

  /**
   * A restore landed: tell the host, which owns the recipe, and reload the list so the version the restore
   * wrote appears where it belongs — at the top, carrying its `Restored` provenance.
   */
  onRestored(event: RecipeVersionRestored): void {
    this.restoreTargetSignal.set(null);

    // Not returned to the trigger: the row it belonged to is about to be replaced by a reloaded list, and
    // the host moves the creator to the restored content anyway.
    this.commandTrigger = null;

    this.restored.emit(event);
    void this.load();
  }

  /**
   * A restore was refused against state this panel and the editor have both moved past. Reloading both is
   * the remedy, and the dialog closes because the version it named may no longer be there to name.
   */
  onRestoreReloadRequested(): void {
    this.restoreTargetSignal.set(null);
    this.commandTrigger = null;
    this.reloadRequested.emit();
    void this.load();
  }

  /** Opens the duplicate dialog for one version. Opening writes nothing; the dialog owns the command. */
  openDuplicate(version: RecipeVersionHistoryEntry, trigger: EventTarget | null): void {
    this.commandTrigger = trigger instanceof HTMLElement ? trigger : null;
    this.duplicateTargetSignal.set(version);
  }

  /** Closed with nothing copied, so the creator is put back on the control they opened it from. */
  closeDuplicate(): void {
    this.duplicateTargetSignal.set(null);
    this.returnFocus();
  }

  /**
   * A copy was made. The history this panel shows is the source's, and copying changed nothing about it — so
   * nothing is reloaded here. The host takes the creator to the copy.
   */
  onDuplicated(event: RecipeDuplicated): void {
    this.duplicateTargetSignal.set(null);
    this.commandTrigger = null;
    this.duplicated.emit(event);
  }

  /** The duplicate dialog naming a version this recipe no longer has: reload the list it was chosen from. */
  onDuplicateReloadRequested(): void {
    this.duplicateTargetSignal.set(null);
    this.commandTrigger = null;
    void this.load();
  }

  /** Swaps the two sides, which is how a creator reads the same pair the other way round. */
  swap(): void {
    const from = this.fromVersion();
    this.fromVersion.set(this.toVersion());
    this.toVersion.set(from);
    this.comparisonStateSignal.set({ status: 'idle' });
  }

  selectFrom(versionNumber: number): void {
    this.fromVersion.set(versionNumber);

    // Any rendered comparison answers the previous pair's question, so it stops being an answer the moment
    // the pair changes.
    this.comparisonStateSignal.set({ status: 'idle' });
  }

  selectTo(versionNumber: number): void {
    this.toVersion.set(versionNumber);
    this.comparisonStateSignal.set({ status: 'idle' });
  }

  /**
   * Whether this is the recipe's current version.
   *
   * Read off the ordering rather than from a published flag: the list is newest first, so the first row of
   * the first page is current — which is why the API does not publish an `isCurrent` it would have to compute
   * per row.
   */
  isCurrent(version: RecipeVersionHistoryEntry): boolean {
    return this.versions()[0]?.id === version.id;
  }

  /** The provenance pill for a version, or null when it was an ordinary creator edit. */
  provenance(version: RecipeVersionHistoryEntry): ProvenanceLabel | null {
    return PROVENANCE[version.source] ?? null;
  }

  readinessTone(version: RecipeVersionHistoryEntry): CpStatusPillTone {
    return version.readiness === 'Ready' ? 'success' : 'progress';
  }

  /**
   * How an author reads when the membership behind the version can no longer be named.
   *
   * A version outlives the membership that wrote it by design, so this is an ordinary state rather than an
   * error — and saying so plainly is better than an empty space the creator has to interpret.
   */
  authorOf(version: RecipeVersionHistoryEntry): string {
    return version.createdByName ?? 'A former member';
  }

  /** The accessible name for one side's radio, which must say which side as well as which version. */
  radioLabel(side: 'from' | 'to', version: RecipeVersionHistoryEntry): string {
    return `Compare ${side} version ${version.versionNumber}`;
  }
}
