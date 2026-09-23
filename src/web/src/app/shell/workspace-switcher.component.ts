import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Router } from '@angular/router';

import { MyWorkspaceMembership } from '../models/auth.models';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';

/**
 * Switching workspaces only changes the route's :workspaceSlug segment — it never sends WorkspaceId in
 * a header or body (see .claude/rules/tenancy.md). The current section (dashboard, recipes, …) is
 * preserved across the switch.
 */
@Component({
  selector: 'cp-workspace-switcher',
  standalone: true,
  template: `
    @if (isLoading()) {
      <span class="status" role="status">Loading workspaces…</span>
    } @else if (isError()) {
      <span class="status status--error">Workspaces unavailable</span>
    } @else if (memberships().length > 0) {
      <label class="switcher">
        <span class="cp-sr-only">Switch workspace</span>
        <select [value]="currentSlug()" (change)="onSelect($event)">
          @for (membership of memberships(); track membership.workspaceId) {
            <option [value]="membership.workspaceSlug">{{ membership.workspaceName }}</option>
          }
        </select>
      </label>
    } @else {
      <span class="status">No workspaces</span>
    }
  `,
  styles: [`
    :host{display:block}
    .status{color:var(--cp-text-muted);font-size:var(--cp-font-size-sm)}
    .status--error{color:var(--cp-danger)}
    .switcher select{min-height:2.5rem;padding:0 var(--cp-space-3);border:1px solid var(--cp-border);border-radius:var(--cp-radius-md);background:var(--cp-surface);color:var(--cp-text);font:600 var(--cp-font-size-sm)/1.2 var(--cp-font-sans)}
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceSwitcherComponent {
  private readonly router = inject(Router);
  private readonly membershipService = inject(WorkspaceMembershipService);

  readonly currentSlug = input<string | null>(null);
  readonly currentSection = input('dashboard');

  readonly isLoading = computed(() => this.membershipService.state().status === 'loading');
  readonly isError = computed(() => this.membershipService.state().status === 'error');
  readonly memberships = computed<readonly MyWorkspaceMembership[]>(() => {
    const state = this.membershipService.state();
    return state.status === 'ready' ? state.memberships : [];
  });

  onSelect(event: Event): void {
    const nextSlug = (event.target as HTMLSelectElement).value;
    if (nextSlug && nextSlug !== this.currentSlug()) {
      void this.router.navigate(['/', nextSlug, this.currentSection()]);
    }
  }
}
