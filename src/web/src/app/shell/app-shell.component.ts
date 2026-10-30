import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, isDevMode, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { CpButtonComponent, CpLoadingBarComponent, CpStatusPillComponent, CpStatusPillTone, CpThemeService } from '@creator-pantry/ui';

import { AiUsageService } from '../services/ai-usage.service';
import { AuthService } from '../services/auth.service';
import { ContentPipelineDraftService } from '../services/content-pipeline-draft.service';
import { ImageStudioDraftService } from '../services/image-studio-draft.service';
import { SocialStudioDraftService } from '../services/social-studio-draft.service';
import { LongFormPipelinePlaceService } from '../services/long-form-pipeline-place.service';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';
import { QuickCaptureComponent } from '../shared/quick-capture/quick-capture.component';
import { QuickCaptureLink, quickCaptureLinkFrom } from '../shared/quick-capture/quick-capture-link';
import { QuickCaptureService } from '../shared/quick-capture/quick-capture.service';
import { WorkspaceSwitcherComponent } from './workspace-switcher.component';
import { CpLogoComponent } from '../shared/logo/logo.component';
import { ShellNavIcon, ShellNavIconComponent } from '../shared/nav-icon/shell-nav-icon.component';

/**
 * The width a routed page asks the shell's frame for, through `data: { pageWidth }` beside its `title`.
 * `standard` is what a route gets by saying nothing (DESIGN-SYSTEM.md, "Composing a page").
 */
export type PageWidth = 'reading' | 'standard' | 'wide';

const PAGE_WIDTHS: readonly string[] = ['reading', 'standard', 'wide'] satisfies readonly PageWidth[];

function isPageWidth(value: unknown): value is PageWidth {
  return typeof value === 'string' && PAGE_WIDTHS.includes(value);
}

/**
 * A tool's tone (AU.1, D1), asked for through `data: { tone }` the same way. The frame sets it as `data-cp-tone`
 * and every part on the page resolves `--cp-tone*` from it; a route that says nothing is untoned, which is the
 * primary (DESIGN-SYSTEM.md, "Warmth inside the app").
 */
export type PageTone = 'purple' | 'pink' | 'blue' | 'orange';

const PAGE_TONES: readonly string[] = ['purple', 'pink', 'blue', 'orange'] satisfies readonly PageTone[];

function isPageTone(value: unknown): value is PageTone {
  return typeof value === 'string' && PAGE_TONES.includes(value);
}

interface ShellNavItem {
  readonly path: string;
  readonly label: string;
  readonly icon: ShellNavIcon;
}

/** A run of sidebar entries under one small heading. The order of the entries across groups is the nav's order. */
interface ShellNavGroup {
  readonly id: string;
  readonly label: string;
  readonly items: readonly ShellNavItem[];
}

const NAV_GROUPS: readonly ShellNavGroup[] = [
  {
    id: 'plan',
    label: 'Plan',
    items: [
      { path: 'dashboard', label: 'Dashboard', icon: 'dashboard' },
      { path: 'workflows', label: 'Workflows', icon: 'workflows' },
      { path: 'my-day', label: 'My Day', icon: 'my-day' },
      { path: 'my-week', label: 'My Week', icon: 'my-week' },
    ],
  },
  {
    id: 'create',
    label: 'Create',
    items: [
      { path: 'recipes', label: 'Recipes', icon: 'recipes' },
      { path: 'brand', label: 'Brand', icon: 'brand' },
      { path: 'ai-recipe-studio', label: 'AI Recipe Studio', icon: 'ai' },
      { path: 'image-studio', label: 'Image Studio', icon: 'image' },
      { path: 'social-studio', label: 'Social Studio', icon: 'social' },
    ],
  },
  {
    id: 'library',
    label: 'Library',
    items: [
      { path: 'dam', label: 'Pictures', icon: 'pictures' },
      { path: 'content-board', label: 'Content Board', icon: 'board' },
      { path: 'prompt-library', label: 'Prompt Library', icon: 'prompts' },
    ],
  },
  {
    id: 'workspace',
    label: 'Workspace',
    items: [{ path: 'settings', label: 'Settings', icon: 'settings' }],
  },
];

const NAV_ITEMS: readonly ShellNavItem[] = NAV_GROUPS.flatMap((group) => group.items);

@Component({
  selector: 'cp-app-shell',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    CpButtonComponent,
    CpLoadingBarComponent,
    CpStatusPillComponent,
    WorkspaceSwitcherComponent,
    CpLogoComponent,
    QuickCaptureComponent,
    ShellNavIconComponent,
  ],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  private readonly usage = inject(AiUsageService);
  /**
   * Injected and otherwise unused, so the service exists to notice a sign-out.
   *
   * It drops every kept Content Pipeline draft when the session goes anonymous, and it can only do that if it
   * has been constructed. Leaving that to whoever opens the pipeline next would mean a creator who signs out
   * without going near it leaves their own unfinished wording on the machine.
   */
  private readonly pipelineDrafts = inject(ContentPipelineDraftService);
  /** Likewise, for the Image Studio's kept work. */
  private readonly imageStudioDrafts = inject(ImageStudioDraftService);
  /** Likewise, for the post edits Social Studio keeps until they are saved. */
  private readonly socialStudioDrafts = inject(SocialStudioDraftService);
  /** Likewise, for where a creator was in the long-form pipeline (LF.6.2): a step and two ids, and still theirs. */
  private readonly longFormPlaces = inject(LongFormPipelinePlaceService);
  readonly theme = inject(CpThemeService);

  readonly navItems = NAV_ITEMS;
  readonly navGroups = NAV_GROUPS;
  readonly isDevMode = isDevMode();
  readonly menuOpen = signal(false);
  readonly session = this.auth.session;

  private readonly routeState = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      startWith(null),
      map(() => this.readRouteState()),
    ),
    { initialValue: this.readRouteState() },
  );

  readonly currentWorkspaceSlug = () => this.routeState().slug;
  readonly currentSection = () => this.routeState().section;
  readonly currentPageTitle = () => this.routeState().title;
  readonly currentPageWidth = () => this.routeState().pageWidth;
  readonly currentPageTone = () => this.routeState().tone;

  /**
   * A navigation is under way — most often a page's code still arriving, since every route loads lazily. The
   * router's, not HTTP's: a page's own reads draw their skeletons, and polling or a background refresh must never
   * light the bar up.
   */
  readonly navigating = computed(() => this.router.currentNavigation() !== null);

  /** The record on the page behind, when quick capture may offer to link a task to it (13A.31). */
  readonly quickCaptureLink = () => this.routeState().link;

  /**
   * Whether the caller can keep work in the current workspace — Contributor and above — and so gets "+ Task". A
   * Viewer is a read-only member: the button is not offered rather than offered and refused.
   */
  readonly canKeepWork = computed(() => {
    const slug = this.currentWorkspaceSlug();
    const state = this.membershipService.state();
    if (!slug || state.status !== 'ready') return false;

    const membership = state.memberships.find((candidate) => candidate.workspaceSlug === slug);
    return membership !== undefined && membership.role !== 'Viewer';
  });

  /**
   * The allowance, shown in the topbar beside the account rather than in the sidebar.
   *
   * The sidebar is workspace-scoped — every item there is a section of the workspace in the URL — and an
   * allowance belongs to the person, spanning all of them. The topbar is where the account already lives.
   */
  readonly allowance = this.usage.allowance;

  /** Only says something when there is something worth saying; a healthy balance needs no chrome. */
  readonly allowanceNeedsAttention = computed(
    () =>
      this.allowance().kind === 'nearly-spent' ||
      this.allowance().kind === 'exhausted' ||
      this.allowance().kind === 'suspended',
  );

  readonly allowanceLabel = computed(() => {
    switch (this.allowance().kind) {
      case 'nearly-spent':
        return 'AI nearly spent';
      case 'exhausted':
        return 'AI allowance spent';
      case 'suspended':
        return 'AI switched off';
      default:
        return 'AI allowance';
    }
  });

  readonly allowanceTone = computed<CpStatusPillTone>(() =>
    this.allowance().kind === 'nearly-spent' ? 'warning' : 'error',
  );

  constructor() {
    void this.membershipService.ensureLoaded();
    void this.usage.ensureLoaded();

    // A page opens the one dialog through the service (13A.32); there is one only while the workspace offers it.
    const unregister = inject(QuickCaptureService).register((options) => this.quickCapture()?.open(options));
    inject(DestroyRef).onDestroy(unregister);
  }

  /** The capture dialog, while the current workspace offers one. */
  private readonly quickCapture = viewChild(QuickCaptureComponent);

  /**
   * Opens quick capture. The button is focused first — a click does not focus a button in every browser — so the
   * dialog gives focus back to it on close.
   */
  protected openQuickCapture(button: HTMLElement): void {
    button.focus();
    this.quickCapture()?.open();
  }

  async signOut(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/sign-in');
  }

  private readRouteState(): {
    slug: string | null;
    section: string;
    title: string;
    pageWidth: PageWidth;
    tone: PageTone | null;
    link: QuickCaptureLink | null;
  } {
    let node = this.route.firstChild;
    let slug: string | null = null;
    let section = 'dashboard';
    let title = 'CreatorPantry';
    let pageWidth: PageWidth = 'standard';
    let tone: PageTone | null = null;
    // Every parameter down the tree, the deepest winning, so the page's own record id is the one read.
    const params = new Map<string, string>();
    // A node can exist in the tree with its `snapshot` not yet assigned: this runs once synchronously
    // during construction (this component and its own '' child both activate in the same navigation),
    // before the router has finished wiring up that child's snapshot. Stop at that point rather than
    // reading through it — the subsequent NavigationEnd-driven recomputation fills in the real values
    // once activation completes.
    while (node?.snapshot) {
      const paramSlug = node.snapshot.paramMap.get('workspaceSlug');
      if (paramSlug) slug = paramSlug;
      const segment = node.snapshot.url[0]?.path;
      if (segment) section = segment;
      const routeTitle = node.snapshot.data['title'];
      if (typeof routeTitle === 'string') title = routeTitle;
      // The deepest route that names a width wins, as it does for the title; a value that is not one of the
      // three steps is ignored rather than reaching the DOM as an attribute no rule matches.
      const routeWidth: unknown = node.snapshot.data['pageWidth'];
      if (isPageWidth(routeWidth)) pageWidth = routeWidth;
      // A tone is set on a section's parent route and its pages inherit it by being below it; the same rule.
      const routeTone: unknown = node.snapshot.data['tone'];
      if (isPageTone(routeTone)) tone = routeTone;
      for (const key of node.snapshot.paramMap.keys) {
        const value = node.snapshot.paramMap.get(key);
        if (value) params.set(key, value);
      }
      node = node.firstChild;
    }
    return { slug, section, title, pageWidth, tone, link: quickCaptureLinkFrom(params) };
  }
}
