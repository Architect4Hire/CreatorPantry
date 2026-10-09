import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BehaviorSubject, combineLatest, firstValueFrom, map, of, switchMap, tap } from 'rxjs';
import { CpBadgeComponent, CpButtonComponent, CpCardComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ClipboardService } from '../../core/clipboard.service';
import { ContentChannel } from '../../models/brand-profile.models';
import {
  PROMPT_IMAGE_KIND_LABELS,
  PROMPT_SOURCE_LABELS,
  PromptDetail,
  imageStudioDraftFromPrompt,
  promptChannelName,
  promptTitle,
} from '../../models/prompt-library.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { CreativeContextService } from '../../services/creative-context.service';
import { ImageStudioDraftOwner, ImageStudioDraftService } from '../../services/image-studio-draft.service';
import { PromptLibraryService } from '../../services/prompt-library.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';

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
 * **Reuse starts new work and leaves the prompt alone.** It makes a creative context that names this prompt as
 * its source, keeps a copy of the text beside it for Image Studio, and goes there. Whatever is unfinished in
 * the studio stays on its own context, so nothing on this page can lose work.
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
  private readonly contexts = inject(CreativeContextService);
  private readonly clipboard = inject(ClipboardService);
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
  private reuseAttempt: { readonly signature: string; readonly key: string } | null = null;

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
   * The saved prompt is not touched. A new creative context is made that names it as its source (AF.3.1), a
   * copy of its words is put beside that context on this device, and the studio is opened on it. Nothing
   * already in the studio is replaced — that work stays on its own context — so there is nothing to ask first.
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
      const draft = imageStudioDraftFromPrompt(prompt, this.channels());
      const channelKey = draft.config.channelKey;

      const outcome = await firstValueFrom(
        this.contexts.create(
          slug,
          {
            ...(channelKey !== null ? { channelKeys: [channelKey] } : {}),
            from: { kind: 'PromptRecord', promptRecordId: prompt.promptRecordId },
          },
          this.reuseKeyFor(slug, prompt.promptRecordId, channelKey),
        ),
      );

      // Another prompt, workspace or person could have arrived while that was being answered, and writing
      // under the owner captured before it would put this prompt's words in the wrong creator's studio.
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

      if (outcome.status !== 'created') {
        // A reused key now names a different request; the next try needs a new one.
        if (outcome.status === 'key_reused') this.reuseAttempt = null;

        this.reuseProblem.set(
          outcome.status === 'forbidden'
            ? 'You do not have permission to start new work in this workspace.'
            : outcome.status === 'source_unavailable'
              ? "This prompt can't be used to start new work right now. It may have been removed."
              : "This couldn't be started right now. Nothing was changed — try once more.",
        );
        return;
      }

      const contextId = outcome.context.id;

      // Asking twice gets the same context back. If the creator has already worked on it in the studio, what
      // they did there is theirs and is not put back to the saved wording.
      if (this.studioDrafts.readKept(owner, contextId).kept === null) {
        const written = this.studioDrafts.writeKept(owner, contextId, { draft, unsent: null });
        if (!written) {
          // A refused write means the session has lapsed. Going to the studio now would open it without the
          // prompt, which would look like it had been lost.
          this.reuseProblem.set("This couldn't be started because you are no longer signed in. Sign in again, then try once more.");
          return;
        }
      }

      this.studioDrafts.rememberContext(owner, contextId);
      await this.router.navigate(['/', slug, ...HANDOFF_ROUTES.imageStudio(contextId)]);
    } finally {
      this.reusing.set(false);
    }
  }

  /** One key per prompt, channel and workspace, reused across retries so a lost answer does not make a second context. */
  private reuseKeyFor(slug: string, promptRecordId: string, channelKey: string | null): string {
    const signature = JSON.stringify([slug, promptRecordId, channelKey]);

    if (this.reuseAttempt?.signature !== signature) this.reuseAttempt = { signature, key: crypto.randomUUID() };

    return this.reuseAttempt.key;
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
