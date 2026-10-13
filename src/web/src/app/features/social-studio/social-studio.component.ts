import { Location } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import {
  CpButtonComponent,
  CpCardComponent,
  CpDialogComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { injectRouteTitle } from '../../core/route-title';
import { CONTENT_PIPELINE_LIMITS } from '../../models/content-pipeline.models';
import { CreativeContextFields } from '../../models/creative-context-fields.models';
import { pictureReferenceOf } from '../../models/creative-context.models';
import { DamAssetSummary, DamKeepRecipe } from '../../models/dam-asset.models';
import { WeeklyTheme } from '../../models/weekly-theme.models';
import { AiUsageService } from '../../services/ai-usage.service';
import { CreativeContextReferenceOutcome, CreativeContextSession } from '../../services/creative-context-session';
import { CreativeContextFiling, CreativeContextFilingStore } from '../../services/device-draft-store';
import { SocialStudioDraftOwner, SocialStudioDraftService } from '../../services/social-studio-draft.service';
import { WeeklyThemesService } from '../../services/weekly-themes.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { ChannelPostChannels, ChannelPostPictures } from '../../shared/channel-posts/channel-post-host';
import {
  ChannelPostRunComponent,
  ChannelPostRunState,
  ChannelPostRunWording,
  emptyChannelPostRunState,
} from '../../shared/channel-posts/channel-post-run.component';
import { CreativeContextSaveNoticeComponent } from '../../shared/creative-context-save-notice/creative-context-save-notice.component';
import { DayNotesNoticeComponent } from '../../shared/day-notes/day-notes-notice.component';
import { RecipePickerComponent } from '../../shared/recipe-picker/recipe-picker.component';
import { CONTEXT_ROUTE_PARAM, HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';
import { DamAssetPickerComponent } from '../dam/dam-asset-picker.component';
import { SocialStudioRecentComponent } from './social-studio-recent.component';

/**
 * `loading` and `unavailable` are how reading the creator's memberships can end, and `not_found` is the one
 * refusal: a workspace that does not exist and one they cannot reach answer alike. `context_not_found` and
 * `context_unavailable` are how reading the piece of work the address names can end — neither opens as an
 * empty studio. `ready` is the studio itself, for a writer and for a reader.
 */
type Phase = 'loading' | 'unavailable' | 'not_found' | 'context_not_found' | 'context_unavailable' | 'ready';

/** The run's sentences, said for a page where what the posts are about is above them rather than a step back. */
export const SOCIAL_STUDIO_POST_WORDING: ChannelPostRunWording = {
  chooseIntro:
    'Pick where these words are going. Your usual channels are already ticked; add or drop any of them. Each channel gets its own post, written to that channel’s own length, about what you chose above.',
  noWork:
    'There is nothing to write about yet. Link a recipe, choose a picture or write a few lines above, and the channels will be offered here.',
  forbidden:
    'You have view-only access to this workspace, so you can read and copy these posts but not write, edit or decide on them. Ask an Owner or Editor for those.',
  reviewHeading: 'Read them through',
  reviewIntro:
    'One post per channel, each on its own. Nothing goes out from here, and nothing counts as yours until you accept it.',
};

/** Where a change to the work's picture has got to. */
type PictureAction =
  | { readonly status: 'idle' }
  | { readonly status: 'working'; readonly sentence: string }
  | { readonly status: 'failed'; readonly sentence: string };

const PICTURE_FAILURES: Readonly<Record<Exclude<CreativeContextReferenceOutcome, 'saved' | 'duplicate'>, string>> = {
  source_unavailable: 'That picture can no longer be used. It may have been removed from the library.',
  limit: 'This piece of work already names as many sources as it can. Remove one to add a picture.',
  forbidden: 'You do not have permission to change this piece of work.',
  held: 'This could not be done yet. Sort out the notice about your unsaved changes below, then try again.',
  unavailable: "This couldn't be done right now. Nothing was changed.",
};

/** How the workspace's themed days loaded. */
type ThemesState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly themes: readonly WeeklyTheme[] }
  | { readonly status: 'unavailable' };

/** Where an attempt to file new work is remembered: for as long as the page is open, and no longer. */
function memoryFiling(): CreativeContextFilingStore {
  let held: CreativeContextFiling | null = null;

  return {
    read: () => held,
    write: (filing) => (held = filing),
    clear: () => (held = null),
  };
}

/**
 * Social Studio: posts for something that already exists (SOCIAL-UI-001 through 004, master 13.3).
 *
 * **What makes it a different screen from the Content Pipeline's posts step** is how a creator gets here. The
 * pipeline reaches posts after an idea, a prompt and pictures; this is the way to write posts for a recipe, a
 * picture in the library, or a few typed lines, without running any of that. One page: what the posts are
 * about, then the posts.
 *
 * **The posts are {@link ChannelPostRunComponent}'s**, shared with the pipeline, so choosing channels, the
 * limit reading, the generated marker and reviewing each channel exist once. What is this page's own is saying
 * what the posts are about, and where the run's inputs come from.
 *
 * **What they are about lives on a creative context**, through `CreativeContextSession` — the linked recipe,
 * the picture, the working title, the brief and the themed day are read from the server and saved back as
 * they change. Work gets its context on the creator's first answer, not on arrival, so opening the page
 * leaves nothing behind. Everything after that is the post routes.
 *
 * **A typed brief is prompt material and nothing else.** It is stored as the piece of work's brief; it never
 * becomes a recipe, a saved prompt or any other source of truth, and the capability is told it is not a
 * recipe (`content.channel-posts` 1.3.0).
 *
 * **A saved prompt is not offered as a starting point.** The posts capability is deliberately not shown saved
 * image prompts — they are words for a renderer, not the creator's about their food — so offering one would
 * be offering something that reaches the model as nothing.
 *
 * **A Viewer reads and copies.** They are not stopped at the door, as they are in the Image Studio: a set of
 * posts is something to read. What they may not do is not offered, and the run says so once.
 *
 * **Two things stay on this device**, keyed by the piece of work: the post request being waited on, and the
 * words of a post edited and not yet saved. They survive a failed save, another channel being written again,
 * a refresh and leaving the page — and leaving with unsaved words still asks, because kept on a device is not
 * saved to the workspace.
 *
 * **The workspace comes from the route and the creator's own membership** (.claude/rules/tenancy.md). Nothing
 * here schedules, files a board card or publishes.
 */
@Component({
  selector: 'cp-social-studio',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpTopicCardComponent,
    ChannelPostRunComponent,
    CreativeContextSaveNoticeComponent,
    DamAssetPickerComponent,
    DayNotesNoticeComponent,
    RecipePickerComponent,
    SocialStudioRecentComponent,
  ],
  templateUrl: './social-studio.component.html',
  styleUrl: './social-studio.component.css',
  providers: [CreativeContextSession],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SocialStudioComponent {
  /** The page's name, from its own route: what `cp-page-head` prints as the one `h1`. */
  protected readonly pageTitle = injectRouteTitle('Social Studio');
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  protected readonly session = inject(CreativeContextSession);
  private readonly drafts = inject(SocialStudioDraftService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly weeklyThemes = inject(WeeklyThemesService);
  private readonly usage = inject(AiUsageService);
  private readonly confirm = inject(ConfirmService);

  /** The page head, for moving focus to the title when the work on screen has just been replaced. */
  private readonly head = viewChild<CpPageHeadComponent>('head');

  protected readonly wording = SOCIAL_STUDIO_POST_WORDING;
  protected readonly limits = CONTENT_PIPELINE_LIMITS;
  protected readonly allowance = this.usage.allowance;

  readonly workspaceSlug = signal('');
  readonly announcement = signal('');

  /** The run's kept state: the request being waited on, and words nobody has saved yet. */
  protected readonly runState = signal<ChannelPostRunState>(emptyChannelPostRunState());

  /** Whether the next run follows the day's standing notes. A choice about one run, so it is never saved. */
  protected readonly applyDayNotes = signal(true);

  /** True while the library is open to choose a picture from. */
  protected readonly picking = signal(false);
  protected readonly pictureAction = signal<PictureAction>({ status: 'idle' });

  /** What the change in hand says, or empty when nothing is being changed and nothing failed. */
  protected readonly pictureSentence = computed(() => {
    const action = this.pictureAction();

    return action.status === 'idle' ? '' : action.sentence;
  });

  protected readonly themes = signal<ThemesState>({ status: 'loading' });

  /** False until this creator's work has been read. */
  private readonly loaded = signal(false);
  /** The creative context the address names, or null for the studio's bare address. */
  private readonly routeContextId = signal<string | null>(null);
  /** Raised by "Start something new", so the panels are rebuilt rather than left holding the last work's answers. */
  private readonly epoch = signal(0);
  /** What is currently loaded — an owner and the context the address names — so it is not read twice. */
  private loadedKey: string | null = null;
  /** That owner. Every write to this device goes under it, never under whoever the route names by then. */
  private loadedOwner: SocialStudioDraftOwner | null = null;
  /** Raised by each open, so an answer to an earlier one lands nowhere. */
  private openTicket = 0;
  /** The context this work was filed on since it was opened, so its address is shown once. */
  private filedContextId: string | null = null;
  /** The workspace the themed days were read for, so the same one is not read twice. */
  private themesSlug: string | null = null;
  /** The one question being asked about leaving, shared by callers that ask at once. */
  private pendingLeave: Promise<boolean> | null = null;
  /** The linked recipe's title, as the picker resolved it: what a download is named after when the work has no name. */
  private readonly namedRecipe = signal<DamKeepRecipe | null>(null);

  readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  /** Whose studio this is: the workspace by id, and the membership reading it. Null until memberships are read. */
  readonly owner = computed<SocialStudioDraftOwner | null>(() => {
    const membership = this.membership();

    return membership === null
      ? null
      : { workspaceId: membership.workspaceId, membershipId: membership.membershipId };
  });

  private readonly ownerKey = computed(() => {
    const owner = this.owner();

    return owner === null ? '' : `${owner.workspaceId}.${owner.membershipId}`;
  });

  /** False for a Viewer, who reads and copies and is offered nothing else. */
  protected readonly canWrite = computed(() => {
    const role = this.membership()?.role ?? null;

    return role !== null && role !== 'Viewer';
  });

  /**
   * A list of one key, so the template can build the panels with `@for` and destroy them when it changes.
   *
   * The run holds a tracker with the last answer in it, and the recipe picker what it last read. Swapping
   * another workspace's work, another member's, or new work underneath them would leave the previous answers
   * on screen; rebuilding is what makes that impossible (.claude/rules/tenancy.md).
   */
  protected readonly panelKeys = computed(() => [`${this.ownerKey()}#${this.epoch()}`]);

  /** Derived rather than set, so the screen cannot be left claiming a phase the data has moved past. */
  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';
    if (this.membership() === null) return 'not_found';

    if (this.routeContextId() !== null) {
      // An id that does not resolve — unknown, or another workspace's — is said, never shown as empty work.
      const open = this.session.open();
      if (open === 'not_found') return 'context_not_found';
      if (open === 'unavailable') return 'context_unavailable';
    }

    return this.loaded() ? 'ready' : 'loading';
  });

  /**
   * The piece of work the posts are about, or null before there is one.
   *
   * Null too while the session is still pointed at a workspace the route has left: in that gap a context id
   * would be one workspace's record asked of another.
   */
  protected readonly creativeContextId = computed(() =>
    this.session.workspace() === this.workspaceSlug() ? (this.session.context()?.id ?? null) : null,
  );

  protected readonly fields = this.session.fields;

  /** The work's themed day, when it has one. What the notice above the run reads the notes for. */
  protected readonly themeKey = computed(() => this.session.context()?.weeklyThemeKey ?? null);

  /** The picture this work draws on, or null. One at a time: choosing another replaces it. */
  protected readonly pictureReference = computed(() => pictureReferenceOf(this.session.context()));

  /**
   * What else this piece of work names that this page has no control for, said so a creator who arrived from
   * somewhere else knows what the posts will and will not be written from.
   *
   * A chosen idea is used. A saved prompt is not — the posts capability is never shown one — and saying
   * nothing would leave a creator believing it was.
   */
  protected readonly otherSources = computed(() => {
    const references = this.session.context()?.references ?? [];
    const ideas = references.filter((reference) => reference.kind === 'RecipeConcept').length;
    const prompts = references.filter((reference) => reference.kind === 'PromptRecord').length;

    return {
      ideas: ideas === 0 ? '' : ideas === 1 ? 'an idea you chose earlier' : `${ideas} ideas you chose earlier`,
      prompts: prompts > 0,
    };
  });

  /** Changes whenever the work's pictures may have, so the run's picture panel is read again. */
  private readonly pictureRevision = computed(() =>
    (this.session.context()?.references ?? [])
      .filter((reference) => reference.mediaAssetId !== null || reference.generatedImageId !== null)
      .map((reference) => reference.id)
      .join(','),
  );

  protected readonly channels = new ChannelPostChannels(this.workspaceSlug);

  protected readonly pictures = new ChannelPostPictures(
    this.workspaceSlug,
    this.creativeContextId,
    (sentence) => this.announcement.set(sentence),
    this.pictureRevision,
  );

  /** The channels this piece of work was set up for where it names any, else the workspace's usual ones. */
  protected readonly channelDefaults = computed<readonly string[]>(() => {
    const named = this.session.context()?.channelKeys ?? [];

    return named.length > 0 ? named : this.channels.defaults();
  });

  /**
   * The days that can be chosen: the live week, and this work's own theme if it has since been retired — a
   * piece made for a theme goes on saying so, and must not be shown as having none.
   */
  protected readonly themeOptions = computed<readonly WeeklyTheme[]>(() => {
    const state = this.themes();
    if (state.status !== 'ready') return [];

    const current = this.fields().weeklyThemeKey;

    return state.themes.filter((theme) => theme.retiredAt === null || theme.key === current);
  });

  /** What a downloaded file is named after: the work's own name, else its recipe's. */
  protected readonly workName = computed(() => {
    const working = this.fields().workingTitle.trim();

    return working !== '' ? working : (this.namedRecipe()?.title ?? '');
  });

  /** True while a post has words nobody has saved. What leaving asks about. */
  protected readonly hasUnsavedWords = computed(() => Object.keys(this.runState().unsaved).length > 0);

  /** Where what the posts are about is, said plainly. Anything needing attention is the save notice's to say. */
  protected readonly savedText = computed(() => {
    if (this.session.context() === null) return 'Nothing is kept until you give the posts something to be about.';

    const save = this.session.save();
    if (save === 'pending' || save === 'saving') return 'Saving…';

    return save === 'saved' ? 'What these posts are about is saved to your workspace.' : 'Not saved yet.';
  });

  protected readonly dashboardLink = computed(() => ['/', this.workspaceSlug(), 'dashboard']);
  protected readonly startLink = computed(() => ['/', this.workspaceSlug(), 'social-studio']);
  protected readonly themedDaysLink = computed(() => ['/', this.workspaceSlug(), 'settings', 'themed-days']);

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          const read = (key: string): string | null => {
            for (const params of maps) {
              const value = params.get(key);
              if (value) return value;
            }
            return null;
          };
          return { slug: read('workspaceSlug'), contextId: read(CONTEXT_ROUTE_PARAM) };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, contextId }) => {
        if (slug === null) throw new Error('SocialStudioComponent route is missing a workspaceSlug segment.');
        if (slug !== this.workspaceSlug()) this.closeWorkspace(slug);
        this.routeContextId.set(contextId);
      });

    // The work is read once the memberships say who is asking, which is also what re-reads it when the
    // workspace or the context in the route changes, or a different person signs in on this browser.
    effect(() => {
      const owner = this.owner();
      const contextId = this.routeContextId();
      const key = `${this.ownerKey()}#${contextId ?? ''}`;
      if (owner === null || key === this.loadedKey) return;

      this.loadedKey = key;
      untracked(() => void this.open(owner, contextId));
    });

    // Work with no context gets one on its first answer; its address then becomes the one that finds it again.
    effect(() => {
      const contextId = this.creativeContextId();
      if (contextId !== null) untracked(() => this.showContextAddress(contextId));
    });

    effect(() => {
      const slug = this.workspaceSlug();
      if (slug === '' || slug === this.themesSlug) return;

      this.themesSlug = slug;
      untracked(() => void this.loadThemes(slug));
    });

    // A browser close or refresh cannot be confirmed by any dialog, so it gets the browser's own.
    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (!this.hasUnsavedWords()) return;

      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnload);
    inject(DestroyRef).onDestroy(() => window.removeEventListener('beforeunload', beforeUnload));
  }

  /**
   * Asked by the route's `CanDeactivate` guard, and by "Start something new".
   *
   * The words are kept on this device either way, so this is not a warning that they will be lost — it says
   * where they are, because "kept here" and "saved to the workspace" are different promises.
   */
  confirmLeaveWithUnsavedWords(): Promise<boolean> {
    if (!this.hasUnsavedWords()) return Promise.resolve(true);

    const count = Object.keys(this.runState().unsaved).length;

    this.pendingLeave ??= this.confirm
      .confirm({
        title: 'Leave with unsaved words?',
        message:
          (count === 1 ? 'You have edited a post without saving it. ' : `You have edited ${count} posts without saving them. `) +
          'Your words are kept on this device and will be here when you come back, but they are not in your workspace until you save them.',
        confirmLabel: 'Leave',
        cancelLabel: 'Stay and save',
      })
      .finally(() => (this.pendingLeave = null));

    return this.pendingLeave;
  }

  protected retryMemberships(): void {
    void this.memberships.load();
  }

  /** Read the piece of work the address names again. */
  protected retryOpen(): void {
    const owner = this.owner();
    if (owner === null) return;

    void this.open(owner, this.routeContextId());
  }

  protected retryThemes(): void {
    void this.loadThemes(this.workspaceSlug());
  }

  protected onTitle(event: Event): void {
    this.answer({ workingTitle: (event.target as HTMLInputElement).value });
  }

  /** The creator's own lines. Kept exactly as typed, and only ever as this piece of work's brief. */
  protected onBrief(event: Event): void {
    this.answer({ workingBrief: (event.target as HTMLTextAreaElement).value });
  }

  /** A themed day is a day and its theme together; choosing none clears both. */
  protected onTheme(event: Event): void {
    const key = (event.target as HTMLSelectElement).value;
    const theme = this.themeOptions().find((option) => option.key === key) ?? null;

    this.answer(theme === null ? { day: null, weeklyThemeKey: null } : { day: theme.day, weeklyThemeKey: theme.key });
    // A different day has different notes, and switching them off was a choice about the last one.
    this.applyDayNotes.set(true);
  }

  protected onRun(state: ChannelPostRunState): void {
    this.runState.set(state);
    this.persistRun();
  }

  protected onAnnounced(sentence: string): void {
    this.announcement.set(sentence);
  }

  protected onRecipeNamed(recipe: DamKeepRecipe | null): void {
    this.namedRecipe.set(recipe);
  }

  protected openPicker(): void {
    this.pictureAction.set({ status: 'idle' });
    this.picking.set(true);
  }

  protected closePicker(): void {
    this.picking.set(false);
  }

  /**
   * Have the work draw on a library picture. It names the asset and copies nothing: what the posts can say
   * about it is whatever is known about what it shows, which the run's picture panel reads and says.
   */
  protected async choosePicture(asset: DamAssetSummary): Promise<void> {
    this.picking.set(false);
    this.pictureAction.set({ status: 'working', sentence: `Adding ${asset.title}…` });

    const ticket = this.openTicket;
    const existing = this.pictureReference();

    if (existing !== null) {
      if (existing.mediaAssetId === asset.id) {
        this.pictureAction.set({ status: 'idle' });
        return;
      }

      // One picture at a time, so the one in place goes first. The asset itself is untouched.
      const removed = await this.session.removeReference(existing.id);
      if (ticket !== this.openTicket) return;
      if (removed !== 'saved') {
        this.failPicture(removed);
        return;
      }
    }

    const added = await this.session.addReference({ kind: 'DamAsset', mediaAssetId: asset.id });
    if (ticket !== this.openTicket) return;

    if (added !== 'saved' && added !== 'duplicate') {
      this.failPicture(added);
      return;
    }

    this.pictureAction.set({ status: 'idle' });
    this.announcement.set(`${asset.title} goes with these posts.`);
  }

  /** Stop the work naming its picture. The picture stays in the library, which is why this does not ask. */
  protected async removePicture(): Promise<void> {
    const existing = this.pictureReference();
    if (existing === null) return;

    this.pictureAction.set({ status: 'working', sentence: 'Removing the picture…' });

    const ticket = this.openTicket;
    const removed = await this.session.removeReference(existing.id);
    if (ticket !== this.openTicket) return;

    if (removed !== 'saved') {
      this.failPicture(removed);
      return;
    }

    this.pictureAction.set({ status: 'idle' });
    this.announcement.set('The picture no longer goes with these posts.');
  }

  /**
   * Leave this piece of work and begin another, on the same screen.
   *
   * Not a navigation: once work has been filed the address shown is the context's while the router still
   * holds the bare one, so a link there would go nowhere. The piece of work left behind is untouched — its
   * posts stay where they are and it is in the recent list.
   */
  protected async startNew(): Promise<void> {
    const owner = this.owner();
    if (owner === null || !(await this.confirmLeaveWithUnsavedWords())) return;

    // Re-read: a different workspace could have arrived behind the dialog.
    const current = this.owner();
    if (current === null || current.workspaceId !== owner.workspaceId || current.membershipId !== owner.membershipId) {
      return;
    }

    this.openTicket++;
    this.loadedKey = `${this.ownerKey()}#`;
    this.loadedOwner = current;
    this.routeContextId.set(null);
    this.filedContextId = null;
    this.session.begin(this.workspaceSlug(), memoryFiling());
    this.location.replaceState(this.router.serializeUrl(this.router.createUrlTree(this.startLink())));
    this.runState.set(emptyChannelPostRunState());
    this.applyDayNotes.set(true);
    this.namedRecipe.set(null);
    this.pictureAction.set({ status: 'idle' });
    this.loaded.set(true);
    this.epoch.update((value) => value + 1);
    this.announcement.set('Started something new. Nothing is filled in.');
    // The button that was pressed is gone with the work it left, so focus goes to the page's title rather
    // than being dropped on the document.
    this.head()?.focusHeading();
  }

  /** Every route node from this one up to the root, so the slug can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }

  /** Forget the workspace on screen, because the route now names another one. */
  private closeWorkspace(slug: string): void {
    this.workspaceSlug.set(slug);
    this.openTicket++;
    this.loadedKey = null;
    this.loadedOwner = null;
    this.filedContextId = null;
    this.session.close();
    this.loaded.set(false);
    this.runState.set(emptyChannelPostRunState());
    this.applyDayNotes.set(true);
    this.namedRecipe.set(null);
    this.picking.set(false);
    this.pictureAction.set({ status: 'idle' });
    this.themes.set({ status: 'loading' });
    this.announcement.set('');
  }

  /**
   * Read one creator's work.
   *
   * With a context in the address, that context is the work: it is read from the server — and a context this
   * workspace does not have is not found, whoever else's it is — and what this device kept for it is laid
   * beside it. Without one, a writer starts with nothing filled in and a reader has only the recent list.
   */
  private async open(owner: SocialStudioDraftOwner, contextId: string | null): Promise<void> {
    const ticket = ++this.openTicket;
    const slug = this.workspaceSlug();

    this.loadedOwner = owner;
    this.filedContextId = null;
    this.loaded.set(false);
    this.runState.set(emptyChannelPostRunState());

    if (contextId === null) {
      // A reader cannot file work, so no session is begun for one: nothing here could then try to.
      if (this.canWrite()) this.session.begin(slug, memoryFiling());
      else this.session.close();
      this.loaded.set(true);
      return;
    }

    await this.session.load(slug, contextId);
    if (ticket !== this.openTicket || this.session.open() !== 'ready') return;

    this.runState.set(this.drafts.readKept(owner, contextId).kept ?? emptyChannelPostRunState());
    this.loaded.set(true);
  }

  /**
   * One of the creator's answers about what the posts are about.
   *
   * The session takes it and sends it. Work that has no context yet is filed on one as soon as there is
   * something in it — and not again after a failure, which is the save notice's "Try again" to ask for, or
   * every keystroke typed while offline would be another request.
   */
  private answer(change: Partial<CreativeContextFields>): void {
    if (!this.loaded() || !this.canWrite()) return;

    const next = { ...this.session.fields(), ...change };
    this.session.set(next);

    const hasAnswer = next.workingTitle.trim() !== '' || next.workingBrief.trim() !== '' || next.weeklyThemeKey !== null;
    if (this.session.context() === null && hasAnswer && this.session.save() === 'saved') void this.session.file();
  }

  /** Show the address that will find this piece of work again, without navigating away from it. */
  private showContextAddress(contextId: string): void {
    if (this.routeContextId() !== null || contextId === this.filedContextId || !this.loaded()) return;

    this.filedContextId = contextId;
    this.location.replaceState(
      this.router.serializeUrl(
        this.router.createUrlTree(['/', this.workspaceSlug(), ...HANDOFF_ROUTES.socialStudio(contextId)]),
      ),
    );
  }

  /** Keep, for the piece of work on screen, the request being waited on and the words nobody has saved. */
  private persistRun(): void {
    // Under the owner whose work was *read*: between a change of workspace and the next render the run on
    // screen still belongs to the previous one, and its last word must not be stored under the new one's key.
    const owner = this.loadedOwner;
    const contextId = this.creativeContextId();
    if (owner === null || contextId === null || !this.loaded()) return;

    const state = this.runState();
    const nothing = state.requestId === null && Object.keys(state.unsaved).length === 0;

    // Nothing to keep is kept as nothing, so merely opening a piece of work leaves no record of it here.
    if (nothing) this.drafts.clearKept(owner, contextId);
    else this.drafts.writeKept(owner, contextId, state);
  }

  private failPicture(outcome: CreativeContextReferenceOutcome): void {
    const sentence = PICTURE_FAILURES[outcome === 'saved' || outcome === 'duplicate' ? 'unavailable' : outcome];

    this.pictureAction.set({ status: 'failed', sentence });
    this.announcement.set(sentence);
  }

  private async loadThemes(slug: string): Promise<void> {
    this.themes.set({ status: 'loading' });

    const week = await this.weeklyThemes.getWeek(slug);
    if (slug !== this.themesSlug) return;

    this.themes.set(week.status === 'found' ? { status: 'ready', themes: week.themes } : { status: 'unavailable' });
  }
}
