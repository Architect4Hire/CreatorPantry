import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  CpDialogComponent,
  CpMenuSelectAction,
  CpMenuSelectComponent,
  CpMenuSelectOption,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { CreateWorkspaceFormComponent } from './create-workspace-form.component';
import { MyWorkspaceMembership } from '../models/auth.models';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';

const CREATE_ACTION_ID = 'create';

/**
 * Switching workspaces only changes the route's :workspaceSlug segment — it never sends WorkspaceId in
 * a header or body (see .claude/rules/tenancy.md). Every switch (including after creating a new
 * workspace from here) lands on that workspace's dashboard rather than preserving the current section,
 * since the section you were on may not exist or make sense for the newly selected workspace.
 *
 * The control itself is the library's `cp-menu-select`; this component only maps memberships to its options
 * and says what choosing one, or "Create workspace…", does.
 */
@Component({
  selector: 'cp-workspace-switcher',
  standalone: true,
  imports: [CpDialogComponent, CpMenuSelectComponent, CpSkeletonComponent, CreateWorkspaceFormComponent],
  template: `
    <!-- Always present, outside the busy line (AU.7): the one place the switcher says it is still loading. -->
    <span class="cp-sr-only" role="status">{{ isLoading() ? 'Loading workspaces…' : '' }}</span>
    @if (isLoading()) {
      <!-- The switcher as it will arrive: one line its width. -->
      <div class="loading" aria-busy="true"><cp-skeleton shape="line" /></div>
    } @else if (isError()) {
      <span class="status status--error">Workspaces unavailable</span>
    } @else if (memberships().length > 0) {
      <cp-menu-select
        label="Switch workspace"
        placeholder="Choose a workspace"
        [options]="options()"
        [value]="currentSlug()"
        [actions]="actions"
        (selected)="onSelect($event)"
        (actionChosen)="onAction($event)"
      />
    } @else {
      <span class="status">No workspaces</span>
    }

    <cp-dialog [open]="creatingWorkspace()" title="Create a new workspace" (closed)="creatingWorkspace.set(false)">
      <cp-create-workspace-form (created)="onWorkspaceCreated($event)" />
    </cp-dialog>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .status {
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
      .status--error {
        color: var(--cp-danger);
      }
      .loading {
        display: flex;
        align-items: center;
        min-height: 2.5rem;
        max-width: 12rem;
      }
      .loading cp-skeleton {
        flex: 1;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceSwitcherComponent {
  private readonly router = inject(Router);
  private readonly membershipService = inject(WorkspaceMembershipService);

  readonly currentSlug = input<string | null>(null);

  readonly creatingWorkspace = signal(false);
  readonly actions: readonly CpMenuSelectAction[] = [{ id: CREATE_ACTION_ID, label: '+ Create workspace…' }];

  readonly isLoading = computed(() => this.membershipService.state().status === 'loading');
  readonly isError = computed(() => this.membershipService.state().status === 'error');
  readonly memberships = computed<readonly MyWorkspaceMembership[]>(() => {
    const state = this.membershipService.state();
    return state.status === 'ready' ? state.memberships : [];
  });
  readonly options = computed<readonly CpMenuSelectOption[]>(() =>
    this.memberships().map((membership) => ({ value: membership.workspaceSlug, label: membership.workspaceName })),
  );

  onSelect(workspaceSlug: string): void {
    void this.router.navigate(['/', workspaceSlug, 'dashboard']);
  }

  onAction(id: string): void {
    if (id === CREATE_ACTION_ID) {
      this.creatingWorkspace.set(true);
    }
  }

  async onWorkspaceCreated(workspaceSlug: string): Promise<void> {
    this.creatingWorkspace.set(false);
    await this.router.navigate(['/', workspaceSlug, 'dashboard']);
  }
}
