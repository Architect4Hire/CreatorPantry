import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { CpButtonComponent, CpEmptyStateComponent } from '@creator-pantry/ui';

import { CreateWorkspaceFormComponent } from './create-workspace-form.component';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';

/**
 * The '' index route inside the shell. Resolves the signed-in user's workspace memberships and either
 * redirects into the first one, or — for a brand-new account with no active membership yet, or a failed
 * fetch — renders the appropriate state right here instead of redirecting nowhere. A brand-new account
 * gets a form to create its first workspace, becoming its Owner atomically (POST /api/v1/workspaces).
 */
@Component({
  selector: 'cp-workspace-gate',
  standalone: true,
  imports: [CpEmptyStateComponent, CpButtonComponent, CreateWorkspaceFormComponent],
  template: `
    @let state = memberships.state();
    @switch (state.status) {
      @case ('loading') {
        <p class="status" role="status">Loading your workspaces…</p>
      }
      @case ('error') {
        <cp-empty-state title="Couldn't load your workspaces" description="Check your connection and try again." icon="⚠">
          <div cpEmptyStateActions>
            <button cpButton (click)="retry()">Try again</button>
          </div>
        </cp-empty-state>
      }
      @case ('ready') {
        @if (state.memberships.length === 0) {
          <cp-empty-state
            title="Create your first workspace"
            description="You don't belong to a workspace yet. Create one to get started, or ask a workspace owner to invite you."
            variant="first-use"
            icon="✦"
          >
            <cp-create-workspace-form cpEmptyStateActions (created)="onCreated($event)" />
          </cp-empty-state>
        }
      }
    }
  `,
  styles: [`
    :host { display: block; padding: var(--cp-space-10); }
    .status { margin: 0; color: var(--cp-text-muted); font-size: var(--cp-font-size-sm); }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceGateComponent {
  private readonly router = inject(Router);
  readonly memberships = inject(WorkspaceMembershipService);

  private readonly redirected = signal(false);
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    void this.loadAndRedirect(() => this.memberships.ensureLoaded());
  }

  retry(): void {
    // An explicit user-initiated retry always forces a fresh load(), rather than relying on
    // ensureLoaded()'s "only reload from loading/error" semantics.
    void this.loadAndRedirect(() => this.memberships.load());
  }

  async onCreated(workspaceSlug: string): Promise<void> {
    await this.router.navigate(['/', workspaceSlug, 'dashboard']);
  }

  private async loadAndRedirect(load: () => Promise<void>): Promise<void> {
    await load();
    // If the user navigated away manually while this load was in flight, don't yank them back —
    // a fire-and-forget async redirect from the constructor has no other way to be cancelled.
    if (this.destroyed) return;

    const state = this.memberships.state();
    if (state.status === 'ready' && state.memberships.length > 0 && !this.redirected()) {
      this.redirected.set(true);
      await this.router.navigate(['/', state.memberships[0].workspaceSlug, 'dashboard']);
    }
  }
}
