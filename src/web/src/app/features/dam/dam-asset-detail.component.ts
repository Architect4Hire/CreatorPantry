import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BehaviorSubject, combineLatest, firstValueFrom, map, of, switchMap, tap } from 'rxjs';
import { CpBadgeComponent, CpButtonComponent, CpCardComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { ContentChannel } from '../../models/brand-profile.models';
import {
  DAM_ASSET_KIND_LABELS,
  DAM_RECIPE_ROLE_LABELS,
  DAM_VERSION_SOURCE_LABELS,
  DamAssetDetail,
  DamAssetPrompt,
  DamAssetRecipeLink,
  DamAssetRemoval,
  DamAssetUtilization,
  DamAssetVersion,
  damCalendarDateText,
  damLinkImpactClauses,
  damPictureOf,
  damPromptName,
  damRemovalWarning,
} from '../../models/dam-asset.models';
import { PROMPT_SOURCE_LABELS } from '../../models/prompt-library.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { DamAssetService } from '../../services/dam-asset.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { fileSizeText, imageFormatText } from '../content-pipeline/content-pipeline-image-presentation';
import { DamAssetEditComponent } from './dam-asset-edit.component';
import { DamAssetLogUseComponent } from './dam-asset-log-use.component';
import { DamAssetThumbnailComponent } from './dam-asset-thumbnail.component';
import { DamAssetUsageHistoryComponent } from './dam-asset-usage-history.component';
import { DamAssetVersionUploadComponent } from './dam-asset-version-upload.component';

type DetailState =
  | { readonly status: 'loading' }
  | { readonly status: 'not_found' }
  | { readonly status: 'error' }
  | { readonly status: 'ready'; readonly asset: DamAssetDetail }
  /** Taken out of the library a moment ago, from this page. Its own routes answer not-found from here on. */
  | { readonly status: 'removed'; readonly removal: DamAssetRemoval };

/** How reading the creator's role can stand, and the two answers it can give. */
type ChangeAccess = 'checking' | 'unknown' | 'view_only' | 'allowed';

/** One fact about the asset the creator filled in. Facts they left blank are not listed at all. */
interface Fact {
  readonly label: string;
  readonly value: string;
}

/**
 * One library asset in full (`:workspaceSlug/dam/:assetId`, DAM-UI-003): its picture, what the creator said
 * about it, where it came from, every version, and where it has been used.
 *
 * **Reading is for every member; three actions are for Contributors and above** (DAM-UI-004): editing what
 * was said about the picture, adding a new version of it, and logging that it was used. Each is a dialog of
 * its own. **Removing it from the library is for Editors and Owners** (DAM-UI-005), and asks first. A Viewer
 * reads all of it and is told why the actions are not there.
 *
 * **Removing is not erasing, and nothing here says "delete".** The asset is taken out of the library; its
 * files, versions, history and links are all kept. Once removed, the page becomes a plain account of what
 * happened and what still points at it, and offers no action on the asset — its own routes answer not-found
 * from then on, so in particular no use can be logged against it.
 *
 * **A save rebinds the page from the server's answer; a new version re-reads it** — in place, without
 * returning to "loading", so the creator's place on the page is kept. Either way the concurrency token on
 * screen is the latest one, which is what the next edit must quote.
 *
 * **Leaving with an unsaved edit or a running upload asks first**: router navigation through
 * `confirmLeave`, and a browser close or refresh through `beforeunload`, which no library can replace.
 *
 * **The picture and every download go through the server's authorized routes, by id.** The picture is fetched
 * as bytes and shown through an object URL; a download is a plain link to the route, which names the file
 * itself. Nothing on this page is, or is built from, a storage address (.claude/rules/media.md).
 *
 * **Lineage names things and links to them.** A recipe is linked by its title; a prompt by its label, to the
 * Prompt Library page that shows its words — this route does not publish them, so this page does not either.
 *
 * **Each part fails on its own.** The picture and the usage history are separate reads with their own states,
 * so one of them failing leaves the rest of the page standing.
 *
 * **Unknown, another workspace's and removed answer alike**, in one sentence, so an asset id cannot be used to
 * ask what a neighbour owns (.claude/rules/tenancy.md). Nothing read for one asset stays on screen for the next.
 */
@Component({
  selector: 'cp-dam-asset-detail',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpNoticeComponent,
    DamAssetEditComponent,
    DamAssetLogUseComponent,
    DamAssetThumbnailComponent,
    DamAssetUsageHistoryComponent,
    DamAssetVersionUploadComponent,
  ],
  templateUrl: './dam-asset-detail.component.html',
  styleUrl: './dam-asset-detail.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamAssetDetailComponent {
  private readonly assets = inject(DamAssetService);
  private readonly brand = inject(BrandProfileService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly workspaceSlug = signal('');
  private readonly assetId = signal('');

  protected readonly state = signal<DetailState>({ status: 'loading' });
  /** Empty when the catalogue could not be read, in which case a channel is shown as its key. */
  protected readonly channels = signal<readonly ContentChannel[]>([]);

  /** Which dialog is open, if any. One at a time: each is a different piece of work. */
  protected readonly dialog = signal<'edit' | 'version' | 'use' | null>(null);
  /** What just happened, said once in a live region that outlives the dialog that caused it. */
  protected readonly notice = signal('');
  /** What opened the dialog, so focus goes back to it when the dialog closes. */
  private dialogOpener: HTMLElement | null = null;

  private readonly editDialog = viewChild(DamAssetEditComponent);
  private readonly versionDialog = viewChild(DamAssetVersionUploadComponent);
  private readonly useDialog = viewChild(DamAssetLogUseComponent);

  /** Something that went wrong with an action on this page, as opposed to inside a dialog. */
  protected readonly problem = signal('');
  protected readonly removing = signal(false);
  /** Raised after a use is logged, which is what makes the usage history read again from the top. */
  protected readonly usageReload = signal(0);
  /** Set when a dialog learns the asset has gone, so the page reads it again once that dialog is closed. */
  private rereadOnClose = false;
  private pendingLeaveConfirm: Promise<boolean> | null = null;

  private readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  /**
   * Whether this creator may change the asset.
   *
   * `checking` and `unknown` are how reading their memberships can stand; `view_only` is a Viewer; `allowed`
   * is Contributor and above — the role both routes require, so the buttons are offered to exactly the people
   * the server will accept them from.
   */
  protected readonly changeAccess = computed<ChangeAccess>(() => {
    const status = this.memberships.state().status;
    if (status === 'loading') return 'checking';
    if (status === 'error') return 'unknown';

    const membership = this.membership();
    if (membership === null) return 'unknown';

    return membership.role === 'Viewer' ? 'view_only' : 'allowed';
  });

  /**
   * Whether this creator may take the asset out of the library: Editors and Owners, the role the route
   * requires. Higher than for the other changes, because removing takes finished work out of every
   * collaborator's library rather than adding to it. A Contributor is simply not offered it.
   */
  protected readonly canRemove = computed(() => {
    const role = this.membership()?.role;

    return role === 'Editor' || role === 'Owner';
  });

  protected readonly removal = computed(() => {
    const state = this.state();

    return state.status === 'removed' ? state.removal : null;
  });

  private readonly retry$ = new BehaviorSubject<void>(undefined);
  private readonly target = computed(() => ({ slug: this.workspaceSlug(), id: this.assetId() }));

  protected readonly asset = computed(() => {
    const state = this.state();

    return state.status === 'ready' ? state.asset : null;
  });

  protected readonly picture = computed(() => {
    const asset = this.asset();

    return asset === null ? null : damPictureOf(asset);
  });

  protected readonly kindLabel = computed(() => {
    const asset = this.asset();

    return asset === null ? '' : DAM_ASSET_KIND_LABELS[asset.kind];
  });

  /** The channel's name, or its key when the catalogue does not hold it. Empty for an asset filed under none. */
  protected readonly channelName = computed(() => {
    const key = this.asset()?.channelKey?.trim() ?? '';
    if (key === '') return '';

    return this.channels().find((channel) => channel.key === key)?.displayName ?? key;
  });

  /**
   * What the creator said about the asset, as label and value. Only what they filled in: a row reading
   * "Rights holder: none" for every blank field would bury the ones that say something.
   */
  protected readonly facts = computed<readonly Fact[]>(() => {
    const asset = this.asset();
    if (asset === null) return [];

    const facts: Fact[] = [];
    const add = (label: string, value: string | null | undefined): void => {
      const text = value?.trim() ?? '';
      if (text !== '') facts.push({ label, value: text });
    };

    add('Description', asset.description);
    add('Alt text', asset.altText);
    add('Channel', this.channelName());
    // Platform and style are the workspace's own keys. No route names them, so they are shown as stored.
    add('Platform', asset.platformKey);
    add('Day', asset.day);
    add('Style', asset.styleKey);
    add('Rights holder', asset.rightsHolder);
    add('Attribution', asset.attributionText);

    return facts;
  });

  protected readonly libraryLink = computed(() => ['/', this.workspaceSlug(), 'dam']);

  protected readonly downloadUrl = computed(() => {
    const asset = this.asset();

    // No current version means no bytes to download, and the route would answer 404.
    return asset === null || asset.currentVersion === null
      ? null
      : this.assets.downloadUrl(this.workspaceSlug(), asset.id);
  });

  constructor() {
    void this.memberships.ensureLoaded();
    void this.loadChannels();

    // A browser close, refresh or full navigation cannot be confirmed by any library: the browser shows its
    // own message. The router's own navigations are `confirmLeave`, through the route guard.
    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (!this.hasWorkInProgress()) return;

      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnload);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', beforeUnload));

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          let slug: string | null = null;
          let id: string | null = null;
          for (const params of maps) {
            slug ??= params.get('workspaceSlug');
            id ??= params.get('assetId');
          }
          return { slug, id };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, id }) => {
        if (!slug || !id) throw new Error('DamAssetDetailComponent route is missing a workspaceSlug or assetId.');

        this.workspaceSlug.set(slug);
        this.assetId.set(id);
      });

    combineLatest([toObservable(this.target), this.retry$])
      .pipe(
        tap(() => {
          // Before the read, so nothing of the previous asset — or the previous workspace — is on screen while
          // the next one is being fetched. Leaving `ready` also destroys the picture and the usage history,
          // which is what releases what they held — and closes either dialog, whose asset this no longer is.
          this.state.set({ status: 'loading' });
          this.dialog.set(null);
          this.dialogOpener = null;
          this.notice.set('');
          this.problem.set('');
          this.removing.set(false);
          this.rereadOnClose = false;
        }),
        switchMap(([target]) =>
          target.slug === '' || target.id === '' ? of(null) : this.assets.detail(target.slug, target.id),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        if (outcome === null) return;

        switch (outcome.status) {
          case 'found':
            this.state.set({ status: 'ready', asset: outcome.asset });
            return;
          case 'not_found':
            this.state.set({ status: 'not_found' });
            return;
          default:
            this.state.set({ status: 'error' });
        }
      });
  }

  protected retry(): void {
    this.retry$.next();
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }

  // ---- Changing the asset ----

  protected openDialog(which: 'edit' | 'version' | 'use', event: Event): void {
    this.dialogOpener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    this.notice.set('');
    this.problem.set('');
    this.dialog.set(which);
  }

  protected closeDialog(): void {
    this.dialog.set(null);

    // Back to the button that opened it, so a keyboard user is not left at the top of the document.
    const opener = this.dialogOpener;
    this.dialogOpener = null;
    if (opener?.isConnected) opener.focus();

    if (this.rereadOnClose) {
      this.rereadOnClose = false;
      void this.refresh();
    }
  }

  /** A use was recorded: say which day, and have the history and the asset read again. */
  protected onUseLogged(use: DamAssetUtilization): void {
    this.notice.set(`Use logged for ${damCalendarDateText(use.utilizedOn)}.`);
    this.usageReload.update((value) => value + 1);
    void this.refresh();
  }

  /**
   * A dialog was told the asset is not in the library any more. It says so itself; the page is read again
   * once that dialog is closed, rather than pulled out from under the sentence the creator is reading.
   */
  protected onAssetGone(): void {
    this.rereadOnClose = true;
  }

  /**
   * Take the asset out of the library, after asking.
   *
   * **Removing is not erasing, and the question says so**: the files, versions and history are kept and every
   * link is left standing. It also says what will be left pointing at it, built from this page's own read of
   * the asset — and that the app cannot bring it back yet, because that is equally true.
   *
   * The answer goes through `ConfirmService`, where Escape and the backdrop both mean "keep it". The token
   * sent is the one from the read the question was built from, so if the asset changed while the question was
   * open the server refuses, and the creator is shown the latest before being asked again.
   */
  protected async remove(): Promise<void> {
    const asset = this.asset();
    if (asset === null || this.removing() || !this.canRemove()) return;

    const { slug, id } = this.target();
    this.notice.set('');
    this.problem.set('');

    const confirmed = await this.confirm.confirm({
      title: 'Remove this picture from the library?',
      message: damRemovalWarning(asset),
      confirmLabel: 'Remove from library',
      cancelLabel: 'Keep it',
    });
    if (!confirmed) return;

    // Another asset or workspace could have arrived behind the question; removing the one captured before it
    // would take the wrong creator's picture out of the wrong library.
    const now = this.target();
    if (now.slug !== slug || now.id !== id || this.state().status !== 'ready') return;

    this.removing.set(true);
    const outcome = await this.assets.remove(slug, id, asset.concurrencyToken);
    this.removing.set(false);

    const after = this.target();
    if (after.slug !== slug || after.id !== id) return;

    switch (outcome.status) {
      case 'removed':
        this.dialog.set(null);
        this.state.set({ status: 'removed', removal: outcome.removal });
        // The button that was pressed is gone with the page it was on, so focus goes to what replaced it.
        setTimeout(() => this.host.nativeElement.querySelector<HTMLElement>('#dam-removed-heading')?.focus());
        return;

      case 'stale':
        this.problem.set(
          'This picture changed since you opened it, so it was not removed. The latest is loaded — check it, then remove it if you still want to.',
        );
        void this.refresh();
        return;

      case 'forbidden':
        this.problem.set('You need Editor access in this workspace to remove a picture. Nothing was removed.');
        return;

      case 'not_found':
        // Gone already, by another route. The page says what any read of it now would.
        this.state.set({ status: 'not_found' });
        return;

      default:
        this.problem.set('The picture could not be removed just now. Nothing was changed, so try again.');
    }
  }

  /** What a removal left pointing at the asset, as the removal's own answer reported it. */
  protected stillPointing(removal: DamAssetRemoval): readonly string[] {
    // Recipes are listed by name beside this, so only what is merely counted is put into words here.
    return damLinkImpactClauses({
      recipeTitles: [],
      recipeCount: Math.max(0, removal.recipeCount - removal.recipes.length),
      brandProfileCount: removal.brandProfileCount,
      testAttachmentCount: removal.testAttachmentCount,
    });
  }

  protected affectedRecipeLink(recipeId: string): readonly string[] {
    return ['/', this.workspaceSlug(), 'recipes', recipeId];
  }

  /** A save succeeded: the server's answer is the asset now, token included. */
  protected onSaved(asset: DamAssetDetail): void {
    this.rebind(asset);
    this.notice.set('Your changes were saved.');
  }

  /** The edit dialog read the asset again to resolve a conflict; the page behind it should not stay stale. */
  protected onRefreshed(asset: DamAssetDetail): void {
    this.rebind(asset);
  }

  /** A version was stored: the picture, the versions list and the token have all moved, so read them. */
  protected onVersionAdded(version: DamAssetVersion): void {
    this.notice.set(`Version ${version.versionNumber} was added. Earlier versions are unchanged.`);
    void this.refresh();
  }

  /**
   * An upload was aborted part-way. Whether it reached the server is not known here, so the asset is read
   * again and the versions list says what is true.
   */
  protected onUploadCancelled(): void {
    void this.refresh();
  }

  /**
   * Whether the router may leave this page. Resolves at once when nothing is in progress, and otherwise asks
   * — through `ConfirmService`, where dismissing means "stay".
   */
  confirmLeave(): Promise<boolean> {
    if (!this.hasWorkInProgress()) return Promise.resolve(true);

    const uploading = this.versionDialog()?.isUploading() ?? false;

    // Concurrent attempts share the open question rather than stacking a second one on top of it.
    this.pendingLeaveConfirm ??= this.confirm
      .confirm(
        uploading
          ? {
              title: 'Leave while this is uploading?',
              message: 'A new version is still being uploaded. Leaving now stops it, and it may not be stored.',
              confirmLabel: 'Leave and stop the upload',
              cancelLabel: 'Stay here',
            }
          : (this.useDialog()?.dirty() ?? false)
            ? {
                title: 'Discard this entry?',
                message: "The use you were logging hasn't been saved yet. If you leave now, it will be lost.",
                confirmLabel: 'Discard entry',
                cancelLabel: 'Keep editing',
              }
            : {
                title: 'Discard your changes?',
                message: "Your changes to this picture's details haven't been saved yet. If you leave now, they'll be lost.",
                confirmLabel: 'Discard changes',
                cancelLabel: 'Keep editing',
              },
      )
      .finally(() => {
        this.pendingLeaveConfirm = null;
      });

    return this.pendingLeaveConfirm;
  }

  private hasWorkInProgress(): boolean {
    return (
      (this.editDialog()?.dirty() ?? false) ||
      (this.versionDialog()?.isUploading() ?? false) ||
      (this.useDialog()?.dirty() ?? false)
    );
  }

  /** Put a newer copy of the same asset on screen, without leaving `ready`. */
  private rebind(asset: DamAssetDetail): void {
    // An answer about an asset the page has since moved away from is not this page's to show.
    if (asset.id !== this.asset()?.id) return;

    this.state.set({ status: 'ready', asset });
  }

  /**
   * Read the asset again in place.
   *
   * Without passing through `loading`: that would tear down the picture, the versions and the usage history
   * and put the creator back at the top, to tell them something they already know happened. A failure here
   * is left quiet — what is on screen is still the last true read, and the next action will read again.
   */
  private async refresh(): Promise<void> {
    const { slug, id } = this.target();
    const outcome = await firstValueFrom(this.assets.detail(slug, id));

    const now = this.target();
    if (now.slug !== slug || now.id !== id) return;

    if (outcome.status === 'found') {
      this.rebind(outcome.asset);
    } else if (outcome.status === 'not_found') {
      this.dialog.set(null);
      this.state.set({ status: 'not_found' });
    }
  }

  // ---- Lineage ----

  protected recipeLink(link: DamAssetRecipeLink): readonly string[] {
    return ['/', this.workspaceSlug(), 'recipes', link.recipeId];
  }

  protected roleLabel(link: DamAssetRecipeLink): string {
    return DAM_RECIPE_ROLE_LABELS[link.role];
  }

  protected promptLink(prompt: DamAssetPrompt): readonly string[] {
    return ['/', this.workspaceSlug(), 'prompt-library', prompt.id];
  }

  protected promptName(prompt: DamAssetPrompt): string {
    return damPromptName(prompt);
  }

  protected promptSource(prompt: DamAssetPrompt): string {
    return PROMPT_SOURCE_LABELS[prompt.source];
  }

  // ---- Versions ----

  protected isCurrent(version: DamAssetVersion): boolean {
    return version.versionNumber === this.asset()?.currentVersion?.versionNumber;
  }

  /** "JPEG · 1600 × 1200 · 205 KB". */
  protected versionFacts(version: DamAssetVersion): string {
    return `${imageFormatText(version.mediaType)} · ${version.width} × ${version.height} · ${fileSizeText(version.sizeBytes)}`;
  }

  protected versionSource(version: DamAssetVersion): string {
    return DAM_VERSION_SOURCE_LABELS[version.source];
  }

  protected versionDownloadUrl(version: DamAssetVersion): string | null {
    const asset = this.asset();

    return asset === null ? null : this.assets.versionDownloadUrl(this.workspaceSlug(), asset.id, version.versionNumber);
  }

  // ---- Internals ----

  private async loadChannels(): Promise<void> {
    const outcome = await this.brand.listContentChannels();
    if (outcome.status === 'found') this.channels.set(outcome.channels);
  }

  /** Every route node from this one up to the root, so both segments can be found wherever they were declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}
