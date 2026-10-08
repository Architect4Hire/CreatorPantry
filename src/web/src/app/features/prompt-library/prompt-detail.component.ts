import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BehaviorSubject, combineLatest, map, of, switchMap, tap } from 'rxjs';
import { CpBadgeComponent, CpButtonComponent, CpCardComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ClipboardService } from '../../core/clipboard.service';
import { ConfirmService } from '../../core/confirm.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { isImageStudioDraftEmpty } from '../../models/image-studio.models';
import {
  PROMPT_IMAGE_KIND_LABELS,
  PROMPT_SOURCE_LABELS,
  PromptDetail,
  imageStudioDraftFromPrompt,
  promptChannelName,
  promptTitle,
} from '../../models/prompt-library.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ImageStudioDraftOwner, ImageStudioDraftService } from '../../services/image-studio-draft.service';
import { PromptLibraryService } from '../../services/prompt-library.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';

type DetailState =
  | { readonly status: 'loading' }
  | { readonly status: 'not_found' }
  | { readonly status: 'error' }
  | { readonly status: 'ready'; readonly prompt: PromptDetail };

/**
 * Whether this creator may start new work from the prompt.
 *
 * `checking` and `unknown` are how reading their memberships can stand; `view_only` is a Viewer, who can read,
 * copy and download but not make pictures; `allowed` is everyone else in the workspace.
 */
type ReuseAccess = 'checking' | 'unknown' | 'view_only' | 'allowed';

/**
 * One saved prompt in full (`:workspaceSlug/prompt-library/:promptRecordId`, PROMPT-UI-003).
 *
 * **A read-only page, because the record is.** A saved prompt is the evidence of what produced a picture, so
 * there is no edit and no delete here or anywhere. The three things a creator can do with one are take a copy
 * of its words, download it, or start something new from it.
 *
 * **Reuse starts new work and leaves the prompt alone.** It writes a fresh Image Studio draft holding a copy
 * of the text and goes there. The studio keeps one draft per person per workspace, so starting from this
 * prompt replaces whatever is unfinished there — which is the one thing on this page that can lose work, and
 * so the one thing that asks first (`ConfirmService`).
 *
 * **Downloads are links to the server's own routes.** The server names the file and sets the disposition;
 * nothing here builds a filename, and nothing here knows where anything is stored.
 *
 * **What was written for the creator is shown as generated**, beside the prompt that counts, so the two are
 * never mistaken for each other.
 *
 * **Unknown and another workspace's answer alike**, in the same sentence, so a prompt id cannot be used to ask
 * what a neighbour owns (.claude/rules/tenancy.md). Nothing read for one prompt stays on screen for the next.
 */
@Component({
  selector: 'cp-prompt-detail',
  standalone: true,
  imports: [DatePipe, RouterLink, CpBadgeComponent, CpButtonComponent, CpCardComponent, CpNoticeComponent],
  templateUrl: './prompt-detail.component.html',
  styleUrl: './prompt-detail.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PromptDetailComponent {
  private readonly prompts = inject(PromptLibraryService);
  private readonly brand = inject(BrandProfileService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly studioDrafts = inject(ImageStudioDraftService);
  private readonly clipboard = inject(ClipboardService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly workspaceSlug = signal('');
  private readonly promptRecordId = signal('');

  protected readonly state = signal<DetailState>({ status: 'loading' });
  /** Empty when the catalogue could not be read, in which case a channel is shown as its key. */
  private readonly channels = signal<readonly ContentChannel[]>([]);

  protected readonly copyMessage = signal('');
  protected readonly copyFailed = signal(false);
  protected readonly reuseProblem = signal('');
  protected readonly reusing = signal(false);

  private readonly retry$ = new BehaviorSubject<void>(undefined);

  private readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  protected readonly reuseAccess = computed<ReuseAccess>(() => {
    const status = this.memberships.state().status;
    if (status === 'loading') return 'checking';
    if (status === 'error') return 'unknown';

    const membership = this.membership();
    // No membership for a workspace whose prompt was just read should not happen; saying "view only" would be
    // a guess, so it reads as not known.
    if (membership === null) return 'unknown';

    return membership.role === 'Viewer' ? 'view_only' : 'allowed';
  });

  protected readonly prompt = computed(() => {
    const state = this.state();

    return state.status === 'ready' ? state.prompt : null;
  });

  protected readonly title = computed(() => promptTitle(this.prompt()?.label ?? null));
  protected readonly channelName = computed(() => promptChannelName(this.channels(), this.prompt()?.channelKey ?? ''));
  protected readonly kindLabel = computed(() => {
    const prompt = this.prompt();

    return prompt === null ? '' : PROMPT_IMAGE_KIND_LABELS[prompt.imageKind];
  });
  protected readonly sourceLabel = computed(() => {
    const prompt = this.prompt();

    return prompt === null ? '' : PROMPT_SOURCE_LABELS[prompt.source];
  });

  /** True when a model wrote a draft and the creator changed it before using it. */
  protected readonly draftDiffers = computed(() => {
    const prompt = this.prompt();

    return prompt !== null && prompt.generatedText !== null && prompt.generatedText !== prompt.text;
  });

  protected readonly libraryLink = computed(() => ['/', this.workspaceSlug(), 'prompt-library']);

  protected readonly recipeLink = computed(() => {
    const recipeId = this.prompt()?.recipeId ?? null;

    return recipeId === null ? null : ['/', this.workspaceSlug(), 'recipes', recipeId];
  });

  protected readonly textDownloadUrl = computed(() => {
    const prompt = this.prompt();

    return prompt === null ? null : this.prompts.textDownloadUrl(this.workspaceSlug(), prompt.promptRecordId);
  });

  protected readonly recordDownloadUrl = computed(() => {
    const prompt = this.prompt();

    return prompt === null ? null : this.prompts.recordDownloadUrl(this.workspaceSlug(), prompt.promptRecordId);
  });

  private readonly target = computed(() => ({ slug: this.workspaceSlug(), id: this.promptRecordId() }));

  constructor() {
    void this.memberships.ensureLoaded();
    void this.loadChannels();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          let slug: string | null = null;
          let id: string | null = null;
          for (const params of maps) {
            slug ??= params.get('workspaceSlug');
            id ??= params.get('promptRecordId');
          }
          return { slug, id };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, id }) => {
        if (!slug || !id) throw new Error('PromptDetailComponent route is missing a workspaceSlug or promptRecordId.');

        this.workspaceSlug.set(slug);
        this.promptRecordId.set(id);
      });

    combineLatest([toObservable(this.target), this.retry$])
      .pipe(
        tap(() => {
          // Before the read, so nothing of the previous prompt — or the previous workspace — is on screen
          // while the next one is being fetched.
          this.state.set({ status: 'loading' });
          this.copyMessage.set('');
          this.copyFailed.set(false);
          this.reuseProblem.set('');
        }),
        switchMap(([target]) => (target.slug === '' || target.id === '' ? of(null) : this.prompts.get(target.slug, target.id))),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        if (outcome === null) return;

        switch (outcome.status) {
          case 'found':
            this.state.set({ status: 'ready', prompt: outcome.prompt });
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

  protected async copy(): Promise<void> {
    const prompt = this.prompt();
    if (prompt === null) return;

    const copied = await this.clipboard.copy(prompt.text);
    // The page may have moved to another prompt while the clipboard was answering.
    if (this.prompt() !== prompt) return;

    this.copyFailed.set(!copied);
    this.copyMessage.set(
      copied ? 'Prompt copied.' : "The prompt couldn't be copied. Select the text and copy it yourself.",
    );
  }

  /**
   * Start new work in Image Studio from this prompt.
   *
   * The saved prompt is not touched: its words are copied into a new draft. An unfinished studio draft is the
   * creator's own work, so replacing it asks first — and dismissing the question means "keep what I have".
   */
  protected async reuse(): Promise<void> {
    const prompt = this.prompt();
    const membership = this.membership();
    if (prompt === null || membership === null || membership.role === 'Viewer' || this.reusing()) return;

    const slug = this.workspaceSlug();
    const owner: ImageStudioDraftOwner = {
      workspaceId: membership.workspaceId,
      membershipId: membership.membershipId,
    };

    this.reusing.set(true);
    this.reuseProblem.set('');

    try {
      const kept = this.studioDrafts.read(owner).draft;

      if (kept !== null && !isImageStudioDraftEmpty(kept)) {
        const replace = await this.confirm.confirm({
          title: 'Replace what is in Image Studio?',
          message:
            'Image Studio already has unfinished work in it. Starting from this prompt throws that away, including any prompt written there. The prompt saved in your library is not changed.',
          confirmLabel: 'Replace it',
          cancelLabel: 'Keep what I have',
        });
        if (!replace) return;

        // Re-read: another prompt, workspace or person could have arrived behind the dialog, and writing under
        // the owner captured before it would replace the wrong creator's work.
        const current = this.membership();
        if (
          this.prompt() !== prompt ||
          this.workspaceSlug() !== slug ||
          current === null ||
          current.workspaceId !== owner.workspaceId ||
          current.membershipId !== owner.membershipId
        ) {
          return;
        }
      }

      const written = this.studioDrafts.write(owner, imageStudioDraftFromPrompt(prompt, this.channels()));
      if (!written) {
        // A refused write means the session has lapsed. Going to the studio now would open it empty, which
        // would look like the prompt had been lost.
        this.reuseProblem.set("This couldn't be started because you are no longer signed in. Sign in again, then try once more.");
        return;
      }

      await this.router.navigate(['/', slug, 'image-studio']);
    } finally {
      this.reusing.set(false);
    }
  }

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
