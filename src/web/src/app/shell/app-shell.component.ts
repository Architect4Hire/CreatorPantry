import { ChangeDetectionStrategy, Component, computed, inject, isDevMode, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { CpButtonComponent, CpThemeService } from '@creator-pantry/ui';

import { AuthService } from '../services/auth.service';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';
import { WorkspaceSwitcherComponent } from './workspace-switcher.component';

interface ShellNavItem {
  readonly path: string;
  readonly label: string;
  readonly icon: string;
}

const NAV_ITEMS: readonly ShellNavItem[] = [
  { path: 'dashboard', label: 'Dashboard', icon: '⌂' },
  { path: 'workflows', label: 'Workflows', icon: '⇄' },
  { path: 'my-day', label: 'My Day', icon: '☀' },
  { path: 'my-week', label: 'My Week', icon: '▦' },
  { path: 'recipes', label: 'Recipes', icon: '⌘' },
  { path: 'brand', label: 'Brand', icon: '◆' },
  { path: 'ai-recipe-studio', label: 'AI Recipe Studio', icon: '✦' },
  { path: 'image-studio', label: 'Image Studio', icon: '▧' },
  { path: 'social-studio', label: 'Social Studio', icon: '◎' },
  { path: 'dam', label: 'DAM', icon: '▤' },
  { path: 'content-board', label: 'Content Board', icon: '▥' },
  { path: 'prompt-library', label: 'Prompt Library', icon: '❝' },
];

@Component({
  selector: 'cp-app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, CpButtonComponent, WorkspaceSwitcherComponent],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  readonly theme = inject(CpThemeService);

  readonly navItems = NAV_ITEMS;
  readonly isDevMode = isDevMode();
  readonly menuOpen = signal(false);
  readonly session = this.auth.session;
  readonly brandMarkSrc = computed(() =>
    this.theme.resolved() === 'dark' ? '/images/logodark.png' : '/images/logo.png',
  );

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

  constructor() {
    void this.membershipService.ensureLoaded();
  }

  async signOut(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/sign-in');
  }

  private readRouteState(): { slug: string | null; section: string; title: string } {
    let node = this.route.firstChild;
    let slug: string | null = null;
    let section = 'dashboard';
    let title = 'CreatorPantry';
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
      node = node.firstChild;
    }
    return { slug, section, title };
  }
}
