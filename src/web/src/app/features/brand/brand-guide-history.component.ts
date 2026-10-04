import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpEmptyStateComponent,
  CpListShellComponent,
  CpListShellState,
  CpNoticeComponent,
  CpNoticeTone,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { WorkspaceRole } from '../../models/auth.models';
import {
  BrandStyleGuideSummary,
  BrandStyleGuideVersionComparison,
  BrandStyleGuideVersionRow,
} from '../../models/brand-style-guide.models';
import { BrandStyleGuideService } from '../../services/brand-style-guide.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { BrandGuideActivateComponent, BrandStyleGuideVersionActivated } from './brand-guide-activate.component';
import { BrandGuideVersionComparisonComponent } from './brand-guide-version-comparison.component';

type HistoryLoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

type ComparisonState =
  | { readonly status: 'idle' }
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly comparison: BrandStyleGuideVersionComparison }
  /** One of the chosen numbers names no version of this guide — the history moved on under the picker. */
  | { readonly status: 'stale' }
  | { readonly status: 'unavailable' };

/** Makes each instance's radio groups unique, so two mounted screens cannot share a selection. */
let nextInstance = 0;

/**
 * `AuthorizationPolicies.WorkspaceOwner` — the bar activation clears, and deliberately above the Editor bar
 * that writing and approving a version clear. Choosing what the whole workspace writes with is one decision
 * per workspace, and it is not an authoring one.
 */
const ACTIVATE_ROLES: readonly WorkspaceRole[] = ['Owner'];

/**
 * A brand style guide's version history, the comparison a creator builds from it, and the one decision that
 * makes a version the voice this workspace writes with.
 *
 * **Read-only until the creator says otherwise.** The list says what each version is — number, Draft or
 * Approved, whether it is the workspace default, whether its citations have gone stale, the creator's own
 * reason — and is where two versions are chosen. The comparison is calculated by the server and rendered by
 * `cp-brand-guide-version-comparison`, which derives nothing of its own: one diff a creator can trust rather
 * than a client's second opinion about it, and it is the diff an activation is decided against.
 *
 * **Activation is reachable here, not performed here.** A row's button opens `cp-brand-guide-activate`, which
 * owns the confirmation, the request and every way it can be refused. The button is offered only on a version
 * the server would accept — approved, citing nothing stale, not already the default, in a guide that is not
 * archived — and only to an Owner, because offering an action nobody can take is its own defect. The server
 * stays the authority: the dialog reports a refusal if any of that is ever wrong.
 *
 * **Nothing here approves a version.** Approval is an Editor's decision and a different screen's.
 *
 * Versions are chosen by number rather than by id, matching the comparison route: a number is unique only
 * within its guide, so one belonging to another guide matches nothing rather than being refused by a check.
 */
@Component({
  selector: 'cp-brand-guide-history',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    CpButtonComponent,
    CpEmptyStateComponent,
    CpListShellComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    BrandGuideActivateComponent,
    BrandGuideVersionComparisonComponent,
  ],
  templateUrl: './brand-guide-history.component.html',
  styleUrl: './brand-guide-history.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandGuideHistoryComponent {
  private readonly guides = inject(BrandStyleGuideService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  private readonly route = inject(ActivatedRoute);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = this.resolveRouteParam('workspaceSlug');
  readonly guideId = this.resolveRouteParam('guideId');

  private readonly loadStateSignal = signal<HistoryLoadState>({ status: 'loading' });
  readonly loadState = this.loadStateSignal.asReadonly();

  private readonly versionsSignal = signal<readonly BrandStyleGuideVersionRow[]>([]);
  readonly versions = this.versionsSignal.asReadonly();

  /**
   * The guide itself, or null when its read failed.
   *
   * Read alongside the history because one fact lives only here: the **id** of the version holding the
   * workspace default, which an activation has to quote. The rows say which version that is; only this says
   * what to name it by.
   */
  private readonly guideSignal = signal<BrandStyleGuideSummary | null>(null);
  readonly guide = this.guideSignal.asReadonly();

  /**
   * The cursor for the page after the one loaded, or null at the end of the history.
   *
   * Kept rather than discarded: a guide with more versions than one page would otherwise hide its older ones
   * with no cue that they exist, which is a degraded state rendered as a complete one (frontend.md).
   */
  private readonly nextCursorSignal = signal<string | null>(null);
  readonly hasOlderVersions = computed(() => this.nextCursorSignal() !== null);

  private readonly loadingOlderSignal = signal(false);
  readonly loadingOlder = this.loadingOlderSignal.asReadonly();

  /** True when the cursor was refused: older versions exist and this screen can no longer page to them. */
  private readonly pagingDegradedSignal = signal(false);
  readonly pagingDegraded = this.pagingDegradedSignal.asReadonly();

  private readonly comparisonStateSignal = signal<ComparisonState>({ status: 'idle' });
  readonly comparisonState = this.comparisonStateSignal.asReadonly();

  /** The version an activation is being composed for, or null when no dialog is open. */
  private readonly activateTargetSignal = signal<BrandStyleGuideVersionRow | null>(null);
  readonly activateTarget = this.activateTargetSignal.asReadonly();

  /** The button the dialog was opened from, so focus can be handed back to it. */
  private activateTrigger: HTMLElement | null = null;

  /** What the screen says after an activation, as one sentence. */
  readonly notice = signal('');
  readonly noticeTone = signal<CpNoticeTone>('success');

  /** Which two versions the creator has chosen, as numbers. Null until the history has loaded. */
  readonly fromVersion = signal<number | null>(null);
  readonly toVersion = signal<number | null>(null);

  private readonly instance = nextInstance++;
  readonly fromGroup = `cp-brand-guide-history-from-${this.instance}`;
  readonly toGroup = `cp-brand-guide-history-to-${this.instance}`;

  constructor() {
    void this.load();

    // Shared with the workspace switcher and loaded in almost every case already. A failure leaves
    // canActivate() false, which hides the button rather than offering one whose permission is unknown.
    void this.membershipService.ensureLoaded();
  }

  // ---- Rendering ----

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
      ? 'This guide could not be found.'
      : "Couldn't load this guide's history. Check your connection and try again.",
  );

  readonly heading = computed(() => {
    const guide = this.guide();

    return guide ? `${guide.displayName} — version history` : 'Version history';
  });

  /**
   * Whether a comparison can be asked for at all.
   *
   * A guide with one version has a history worth reading and nothing to compare it against, so the list
   * renders and the picker does not — which is why this is separate from the list having rows.
   */
  readonly canChoose = computed(() => this.versions().length >= 2);

  readonly canCompare = computed(() => this.fromVersion() !== null && this.toVersion() !== null);

  /**
   * Whether both sides name the same version. Not an error — the server answers "no changes" — but worth
   * saying before the request, because the answer is predictable.
   */
  readonly comparingWithItself = computed(() => this.canCompare() && this.fromVersion() === this.toVersion());

  readonly comparing = computed(() => this.comparisonState().status === 'loading');

  /**
   * Whether this member could make a version the workspace default at all.
   *
   * Owner only. A Viewer, Contributor or Editor is shown no button rather than one that can only answer 403.
   */
  readonly canActivate = computed(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug)?.role;

    return role !== undefined && ACTIVATE_ROLES.includes(role);
  });

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

        // The selection itself, so a screen-reader user who has just moved a radio hears which pair they are
        // about to compare rather than only that the control moved.
        return from !== null && to !== null
          ? `Comparing version ${from} with version ${to}.`
          : 'Choose two versions and select Compare.';
      }
      default:
        return '';
    }
  });

  /** The pair the compare button will ask about, in words, for the row beside the button. */
  readonly selectionLabel = computed(() => {
    const from = this.fromVersion();
    const to = this.toVersion();

    return from === null || to === null ? 'Choose two versions' : `${from} → ${to}`;
  });

  // ---- Reading ----

  async load(): Promise<void> {
    this.loadStateSignal.set({ status: 'loading' });
    this.pagingDegradedSignal.set(false);

    // Both reads together: the rows and the active version's id are one question for the creator, and two
    // sequential round trips would render a list whose activation buttons could not be offered yet.
    const [guide, versions] = await Promise.all([
      this.guides.get(this.workspaceSlug, this.guideId),
      this.guides.listVersions(this.workspaceSlug, this.guideId),
    ]);

    this.guideSignal.set(guide.status === 'ok' ? guide.guide : null);

    if (versions.status !== 'ok') {
      // A cursor cannot be refused on a first page, so what remains is a guide this workspace cannot read —
      // unknown and another workspace's, indistinguishable by design — or a request that did not arrive.
      this.loadStateSignal.set(
        versions.status === 'not_found' ? { status: 'not_found' } : { status: 'unavailable' },
      );

      return;
    }

    const items = versions.page.items;
    this.versionsSignal.set(items);
    this.nextCursorSignal.set(versions.page.nextCursor);
    this.loadStateSignal.set({ status: 'ready' });

    // A reload answers a different question from the one any rendered comparison answered, and the versions
    // it named may no longer exist. Clearing it is what keeps the panel from outliving its question.
    this.comparisonStateSignal.set({ status: 'idle' });

    // Newest against the one before it: the comparison a creator opening this screen almost always wants, and
    // the only pair that can be preselected without guessing. A guide with one version preselects nothing.
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
   * Separate from {@link load} rather than folded into it: reloading answers "what does the history look like
   * now" and resets the picker, while this one only widens the choice a creator already has.
   */
  async loadOlder(): Promise<void> {
    const cursor = this.nextCursorSignal();
    if (cursor === null || this.loadingOlderSignal()) return;

    this.loadingOlderSignal.set(true);

    try {
      const outcome = await this.guides.listVersions(this.workspaceSlug, this.guideId, cursor);

      if (outcome.status !== 'ok') {
        // A cursor the server will not accept means the history moved on beneath it. The offer stops rather
        // than leaving a button that can only fail, and the screen says so instead of looking complete.
        this.nextCursorSignal.set(null);
        this.pagingDegradedSignal.set(true);

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

    const outcome = await this.guides.compareVersions(this.workspaceSlug, this.guideId, from, to);

    switch (outcome.status) {
      case 'ok':
        this.comparisonStateSignal.set({ status: 'ready', comparison: outcome.comparison });
        this.revealComparison();
        break;
      case 'version_gone':
        this.comparisonStateSignal.set({ status: 'stale' });
        break;
      default:
        this.comparisonStateSignal.set({ status: 'unavailable' });
        break;
    }
  }

  // ---- Choosing ----

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

  /** The accessible name for one side's radio, which must say which side as well as which version. */
  radioLabel(side: 'from' | 'to', version: BrandStyleGuideVersionRow): string {
    return `Compare ${side} version ${version.versionNumber}`;
  }

  // ---- One version's state, in words ----

  statusTone(version: BrandStyleGuideVersionRow): CpStatusPillTone {
    return version.status === 'Approved' ? 'success' : 'neutral';
  }

  statusGlyph(version: BrandStyleGuideVersionRow): string {
    return version.status === 'Approved' ? '✓' : '✎';
  }

  /**
   * What a stale row says, in the creator's terms.
   *
   * "Stale" is a word about data; what it means here is that the examples this version was written from are
   * not the ones in the library any more. The count is the server's — this screen never works it out.
   */
  staleLabel(version: BrandStyleGuideVersionRow): string {
    return version.staleSourceCount === 1
      ? 'An example it used has been replaced'
      : `${version.staleSourceCount} examples it used have been replaced`;
  }

  /**
   * Whether this version can be made the workspace default.
   *
   * The same four conditions the server checks before it writes, so the button is absent where it could only
   * be refused — not because the client is the authority, but because an offer that cannot be taken is worse
   * than no offer. The guide must also have been read: without it there is no active-version id to quote, and
   * inventing one is how somebody else's decision gets overwritten.
   */
  canActivateVersion(version: BrandStyleGuideVersionRow): boolean {
    const guide = this.guide();

    return (
      this.canActivate() &&
      guide !== null &&
      !guide.isArchived &&
      version.status === 'Approved' &&
      version.staleSourceCount === 0 &&
      !version.isActive
    );
  }

  /** Why the Owner is not being offered this version, where saying so is more use than silence. */
  activationBlockedReason(version: BrandStyleGuideVersionRow): string | null {
    if (!this.canActivate() || version.isActive) return null;

    const guide = this.guide();
    if (guide !== null && guide.isArchived) return 'This guide is archived.';
    if (version.status !== 'Approved') return 'Approve this version before it can be the default.';
    if (version.staleSourceCount > 0) return 'Rewrite it from the current examples before making it default.';

    return null;
  }

  // ---- Activating ----

  openActivate(version: BrandStyleGuideVersionRow, trigger: EventTarget | null): void {
    this.activateTrigger = trigger instanceof HTMLElement ? trigger : null;
    this.notice.set('');
    this.activateTargetSignal.set(version);
  }

  closeActivate(): void {
    this.activateTargetSignal.set(null);

    // CpDialogComponent moves focus in and does not return it, so a dialog closed with nothing done would
    // otherwise drop focus to the top of the document.
    this.activateTrigger?.focus();
    this.activateTrigger = null;
  }

  async onActivated(event: BrandStyleGuideVersionActivated): Promise<void> {
    this.activateTargetSignal.set(null);
    this.activateTrigger = null;

    this.noticeTone.set(event.alreadyActive ? 'neutral' : 'success');
    this.notice.set(
      event.alreadyActive
        ? `Version ${event.versionNumber} was already what this workspace writes with. Nothing changed.`
        : event.replacedVersionNumber === null
          ? `This workspace now writes with version ${event.versionNumber}.`
          : `This workspace now writes with version ${event.versionNumber}, replacing version ${event.replacedVersionNumber}.`,
    );

    // The active marker moved, so the list and the guide both describe a state that has changed.
    await this.load();
  }

  /** An activation refused against stale state: re-read the guide and the history it was chosen from. */
  async onActivateReloadRequested(): Promise<void> {
    this.activateTargetSignal.set(null);
    this.activateTrigger = null;
    await this.load();
  }

  // ---- Focus ----

  /**
   * Moves to the comparison once it exists.
   *
   * On a long history the two chosen rows and the answer cannot be on screen together: a creator picks From
   * near the top, To twenty rows down, and the result lands below both. The live region says it arrived, which
   * is no help to anyone who can see — and leaving focus on the button strands a keyboard user above a result
   * they then have to hunt for. The region is focused rather than the heading inside it, which reads the
   * heading out first and needs nothing from the comparison component.
   */
  private revealComparison(): void {
    afterNextRender(
      () => {
        const region = this.elementRef.nativeElement.querySelector<HTMLElement>('.comparison-region');
        region?.focus();
        region?.scrollIntoView({ block: 'nearest' });
      },
      { injector: this.injector },
    );
  }

  private resolveRouteParam(name: string): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const value = node.snapshot.paramMap.get(name);
      if (value) return value;
    }

    throw new Error(`BrandGuideHistoryComponent route is missing a ${name} segment.`);
  }
}
