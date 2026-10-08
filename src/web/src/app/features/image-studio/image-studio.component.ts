import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpCardComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  ContentPipelineConfig,
  ContentPipelineDocumentRef,
  ContentPipelineImagesState,
  ContentPipelinePromptState,
} from '../../models/content-pipeline.models';
import { avoidTextFor } from '../../models/generated-image.models';
import { ImageStudioDraft, emptyImageStudioDraft, isImageStudioDraftEmpty } from '../../models/image-studio.models';
import { ImageStudioDraftOwner, ImageStudioDraftService } from '../../services/image-studio-draft.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { ContentPipelineConceptPanelComponent } from '../content-pipeline/content-pipeline-concept-panel.component';
import { ContentPipelineDocumentPickerComponent } from '../content-pipeline/content-pipeline-document-picker.component';
import { ContentPipelinePromptPanelComponent } from '../content-pipeline/content-pipeline-prompt-panel.component';
import { ContentPipelineReferencePanelComponent } from '../content-pipeline/content-pipeline-reference-panel.component';
import { ContentPipelineSetupStepComponent } from '../content-pipeline/content-pipeline-setup-step.component';
import {
  GeneratedImageRunComponent,
  GeneratedImageRunWording,
} from '../content-pipeline/generated-image-run.component';

/**
 * `loading` and `unavailable` are how reading the creator's memberships can end; `not_found` and `read_only`
 * are the two refusals; `ready` is the studio itself.
 */
type Phase = 'loading' | 'unavailable' | 'not_found' | 'read_only' | 'ready';

/** The parts of the page a creator can jump between, in the order the work usually happens. */
const SECTIONS: readonly CpAnchorNavItem[] = [
  { targetId: 'cp-studio-brief', label: 'The brief' },
  { targetId: 'cp-studio-look', label: 'The look' },
  { targetId: 'cp-studio-extras', label: 'Extras' },
  { targetId: 'cp-studio-prompt', label: 'The prompt' },
  { targetId: 'cp-studio-images-make', label: 'The pictures' },
];

/** The run's sentences, said for a page where everything it refers to is above it rather than a step back. */
const STUDIO_RUN_WORDING: GeneratedImageRunWording = {
  makeIntro: 'This is the prompt above, exactly as it will be sent. Change it there if it is not right yet.',
  noPrompt: 'There is no prompt yet. Write one above, or have one written for you.',
  tooLongRemedy: 'Shorten it above.',
  countRemedy: 'Change that in the brief.',
  forbidden: 'You do not have permission to make pictures in this workspace. Ask an Owner or Editor to make them.',
  keepNote:
    'Keeping a picture here marks it as one you want. Nothing is filed in your library from this page.',
};

/**
 * The Image Studio: from a description of a shot to the pictures worth keeping (IMAGE-UI-001 through 004).
 *
 * **One page, not a journey.** It is the client for the same four contracts as the Content Pipeline's prompt
 * and images steps — looks planned for a shot (IMG-001), a prompt composed and then the creator's (IMG-002), a
 * reference photograph read (IMG-004), and a run of pictures asked for, looked at and declined
 * (IMG-003, IMG-005/006) — without an idea step before them or a posts step after. Every section is on screen
 * at once, because a creator who already knows the prompt they want has no use for being walked to it.
 *
 * **Which is the one behaviour that differs from the pipeline: the prompt box is always here.** A prompt the
 * creator types themselves is as finished as one written for them, and needs no look picked first. Having one
 * *written* still does, because that is what IMG-002 composes from.
 *
 * **It owns the kept draft; the panels own their fields.** Each is handed its part and hands back the next one,
 * so there is exactly one copy of the creator's answers and what is on screen is always what is kept. The
 * panels are the pipeline's own, imported rather than copied — their request tracking, staleness guards and
 * allowance handling are the same work on both screens.
 *
 * **Nothing here files a picture in the library, saves a prompt to the prompt library, or writes a post.**
 * Each is a separate action with its own contract. A kept picture is a mark on this draft and is still
 * `Staged` server-side, which the page says.
 *
 * **Nothing here cancels a generation, because no route does.** "Stop checking" stops this page asking; leaving
 * the page, changing workspace and starting over all abandon whatever was in flight, and an abandoned answer
 * never lands on what the creator is looking at next.
 *
 * **Leaving costs nothing, so leaving is not confirmed.** Every change is kept as it is made. Starting over
 * does ask, because that is the one action that throws the draft away.
 *
 * **The workspace comes from the route and the creator's own membership.** An unknown workspace and one they
 * cannot reach answer alike, so neither discloses that the other exists (.claude/rules/tenancy.md).
 */
@Component({
  selector: 'cp-image-studio',
  standalone: true,
  imports: [
    RouterLink,
    CpAnchorNavComponent,
    CpButtonComponent,
    CpCardComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    ContentPipelineConceptPanelComponent,
    ContentPipelineDocumentPickerComponent,
    ContentPipelinePromptPanelComponent,
    ContentPipelineReferencePanelComponent,
    ContentPipelineSetupStepComponent,
    GeneratedImageRunComponent,
  ],
  templateUrl: './image-studio.component.html',
  styleUrl: './image-studio.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ImageStudioComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly drafts = inject(ImageStudioDraftService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirm = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly sections = SECTIONS;
  protected readonly runWording = STUDIO_RUN_WORDING;

  readonly workspaceSlug = signal('');
  readonly draft = signal<ImageStudioDraft>(emptyImageStudioDraft());
  readonly announcement = signal('');
  /** True when something was stored here that could not be read, so the creator is told rather than puzzled. */
  readonly discarded = signal(false);
  /** The section last jumped to, so the navigation can say where the creator is. */
  protected readonly activeSection = signal<string | null>(null);

  /** False until this creator's kept draft for this workspace has been read. */
  private readonly loaded = signal(false);
  /** The owner key whose draft is currently loaded, so the same one is not read twice. */
  private loadedKey: string | null = null;
  /** That owner. Every write goes under it — see `keep`. */
  private loadedOwner: ImageStudioDraftOwner | null = null;
  /** Raised by Start over, so the panels are rebuilt rather than left holding the answers just thrown away. */
  private readonly epoch = signal(0);

  readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  /**
   * Whose studio this is: the workspace by id, and the membership reading it.
   *
   * Null until the creator's memberships have been read, which is why the draft cannot be loaded from the route
   * alone — the slug names a workspace, and a kept draft belongs to one *person* in one workspace.
   */
  readonly owner = computed<ImageStudioDraftOwner | null>(() => {
    const membership = this.membership();

    return membership === null
      ? null
      : { workspaceId: membership.workspaceId, membershipId: membership.membershipId };
  });

  private readonly ownerKey = computed(() => {
    const owner = this.owner();

    return owner === null ? '' : `${owner.workspaceId}.${owner.membershipId}`;
  });

  /**
   * A list of one key, so the template can build the panels with `@for` and destroy them when it changes.
   *
   * The panels resume their requests once, when they are built, and each holds a tracker with the last answer
   * in it. Swapping a new draft underneath them — another workspace's, another member's, or the empty one after
   * Start over — would leave the previous answers on screen under the new draft, and the prompt panel would
   * write the old composition straight back into it. Rebuilding is what makes that impossible
   * (.claude/rules/tenancy.md).
   */
  protected readonly panelKeys = computed(() => [`${this.ownerKey()}#${this.epoch()}`]);

  /**
   * Derived rather than set, so the screen cannot be left claiming a phase the data has moved past.
   *
   * **A Viewer is stopped at the door rather than led through.** Every ask on this page needs the Contributor
   * role, so a Viewer would meet a refusal behind each button; saying so once is kinder than that.
   */
  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';

    const membership = this.membership();
    if (membership === null) return 'not_found';
    if (membership.role === 'Viewer') return 'read_only';

    return this.loaded() ? 'ready' : 'loading';
  });

  protected readonly prompt = computed(() => this.draft().prompt);
  protected readonly config = computed(() => this.draft().config);

  /**
   * What the run will tell the provider to avoid, or null.
   *
   * It came with the composition the creator was offered, so it is a choice they already saw — and the run
   * shows it beside the prompt before anything is sent.
   */
  protected readonly avoidText = computed(() => avoidTextFor(this.prompt().generated?.avoid ?? []));

  protected readonly canStartOver = computed(() => !isImageStudioDraftEmpty(this.draft()));

  readonly savedText = computed(() => {
    const savedAt = this.draft().savedAt;
    const at = savedAt === '' ? null : new Date(savedAt);
    if (at === null || Number.isNaN(at.getTime())) {
      return 'This is not being kept on this device right now. Signing in again will keep what you do next.';
    }

    const when = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(at);
    return `Kept on this device at ${when}.`;
  });

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          for (const params of maps) {
            const value = params.get('workspaceSlug');
            if (value) return value;
          }
          return null;
        }),
        takeUntilDestroyed(),
      )
      .subscribe((slug) => {
        if (slug === null) throw new Error('ImageStudioComponent route is missing a workspaceSlug segment.');
        if (slug !== this.workspaceSlug()) this.closeWorkspace(slug);
      });

    // The draft is read once the memberships say who is asking, which is also what re-reads it when the
    // workspace in the route changes or a different person signs in on this browser.
    effect(() => {
      const owner = this.owner();
      if (owner === null || this.ownerKey() === this.loadedKey) return;

      this.loadedKey = this.ownerKey();
      this.open(owner);
    });
  }

  /** Every route node from this one up to the root, so the slug can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }

  /**
   * Forget the workspace on screen, because the route now names another one.
   *
   * Everything read for the previous one goes — nothing of one workspace may still be showing while the next
   * one's draft is being read.
   */
  private closeWorkspace(slug: string): void {
    this.workspaceSlug.set(slug);
    this.loadedKey = null;
    this.loadedOwner = null;
    this.loaded.set(false);
    this.draft.set(emptyImageStudioDraft());
    this.discarded.set(false);
    this.announcement.set('');
    this.activeSection.set(null);
  }

  /** Read one creator's kept draft for one workspace. The read is synchronous; what is waited on is who asks. */
  private open(owner: ImageStudioDraftOwner): void {
    const { draft: kept, discarded } = this.drafts.read(owner);
    this.loadedOwner = owner;

    this.discarded.set(discarded);
    this.draft.set(kept ?? emptyImageStudioDraft());
    this.loaded.set(true);
  }

  /** Keep the draft and write it through, so what is on screen and what is kept never differ. */
  private keep(next: ImageStudioDraft): void {
    // Written under the owner whose draft was *read*, never the one the route names right now. Between a change
    // of workspace and the next render, the panels on screen still belong to the previous owner; anything one
    // of them emits in that gap would otherwise be stored under the new owner's key (.claude/rules/tenancy.md).
    const owner = this.loadedOwner;
    if (owner === null || !this.loaded() || this.ownerKey() !== this.loadedKey) return;

    const stamped: ImageStudioDraft = { ...next, savedAt: new Date().toISOString() };
    // A refused write — the session has lapsed — keeps the edit on screen but not the claim that it was kept.
    const written = this.drafts.write(owner, stamped);
    this.draft.set(written ? stamped : { ...next, savedAt: '' });
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }

  protected onConfig(config: ContentPipelineConfig): void {
    // The day is not asked here, so whatever a shared panel hands back, none is kept.
    this.keep({ ...this.draft(), config: { ...config, day: null } });
  }

  protected onPrompt(prompt: ContentPipelinePromptState): void {
    this.keep({ ...this.draft(), prompt });
  }

  protected onBrief(brief: ContentPipelineDocumentRef | null): void {
    this.onPrompt({ ...this.prompt(), brief });
  }

  protected onImages(images: ContentPipelineImagesState): void {
    this.keep({ ...this.draft(), images });
  }

  protected onAnnounced(sentence: string): void {
    this.announcement.set(sentence);
  }

  /**
   * Go to one part of the page.
   *
   * The navigation leaves scrolling and focus to its consumer. `scrollIntoView` with no `behavior` lets the
   * stylesheet decide, which is how global.css honours `prefers-reduced-motion`; focus then lands on the
   * section itself, so a keyboard or screen-reader user arrives where a sighted one was taken.
   */
  protected onSectionActivated(item: CpAnchorNavItem): void {
    this.activeSection.set(item.targetId);

    const target = this.host.nativeElement.querySelector<HTMLElement>(`#${CSS.escape(item.targetId)}`);
    target?.scrollIntoView({ block: 'start' });
    target?.focus({ preventScroll: true });
  }

  /**
   * Throw the draft away and begin again.
   *
   * The one action here that loses work, so it is the one that asks — through `ConfirmService`, where Escape and
   * the backdrop both mean "stay" (.claude/rules/design-system.md). Pictures already made are not declined by
   * this: they stay where they are until they are collected, and the sentence says so rather than implying a
   * clean-up that does not happen.
   */
  protected async startOver(): Promise<void> {
    const owner = this.owner();
    if (owner === null || !this.canStartOver()) return;

    const discard = await this.confirm.confirm({
      title: 'Start this over?',
      message:
        'Everything you have filled in here will be thrown away, including your prompt. Pictures already made are not removed by this — they simply stop being shown here.',
      confirmLabel: 'Throw it away',
      cancelLabel: 'Keep what I have',
    });
    if (!discard) return;

    // Re-read: a different workspace could have arrived behind the dialog, and clearing the owner captured
    // before it would throw away the wrong creator's work.
    const current = this.owner();
    if (current === null || current.workspaceId !== owner.workspaceId || current.membershipId !== owner.membershipId) {
      return;
    }

    this.drafts.clear(current);
    this.draft.set(emptyImageStudioDraft());
    this.discarded.set(false);
    this.activeSection.set(null);
    this.epoch.update((value) => value + 1);
    this.announcement.set('Started over. Nothing is filled in.');
  }
}
