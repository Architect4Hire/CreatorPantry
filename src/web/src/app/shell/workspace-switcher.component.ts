import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { CpDialogComponent } from '@creator-pantry/ui';

import { CreateWorkspaceFormComponent } from './create-workspace-form.component';
import { MyWorkspaceMembership } from '../models/auth.models';
import { WorkspaceMembershipService } from '../services/workspace-membership.service';

const CREATE_OPTION_VALUE = '__create__';

/**
 * Switching workspaces only changes the route's :workspaceSlug segment — it never sends WorkspaceId in
 * a header or body (see .claude/rules/tenancy.md). Every switch (including after creating a new
 * workspace from here) lands on that workspace's dashboard rather than preserving the current section,
 * since the section you were on may not exist or make sense for the newly selected workspace.
 */
@Component({
  selector: 'cp-workspace-switcher',
  standalone: true,
  imports: [CpDialogComponent, CreateWorkspaceFormComponent],
  template: `
    @if (isLoading()) {
      <span class="status" role="status">Loading workspaces…</span>
    } @else if (isError()) {
      <span class="status status--error">Workspaces unavailable</span>
    } @else if (memberships().length > 0) {
      <label class="switcher">
        <span class="cp-sr-only">Switch workspace</span>
        <!--
          [selected] on each option, not [value] on the <select>: a workspace just created here adds
          its own <option> in the same change-detection pass that also wants to select it. A [value]
          write on the <select> can land before that new <option> exists in the DOM, in which case the
          browser silently falls back to the first option — and Angular never retries, since its own
          tracked expression value hasn't changed on a later pass. Binding [selected] per option has no
          such ordering to race: each option carries its own correct state as soon as it exists.
        -->
        <select (change)="onSelect($event)">
          @for (membership of memberships(); track membership.workspaceId) {
            <option [value]="membership.workspaceSlug" [selected]="membership.workspaceSlug === currentSlug()">{{ membership.workspaceName }}</option>
          }
          <option [value]="createOptionValue">+ Create workspace…</option>
        </select>
      </label>
    } @else {
      <span class="status">No workspaces</span>
    }

    <cp-dialog [open]="creatingWorkspace()" title="Create a new workspace" (closed)="creatingWorkspace.set(false)">
      <cp-create-workspace-form (created)="onWorkspaceCreated($event)" />
    </cp-dialog>
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

  readonly createOptionValue = CREATE_OPTION_VALUE;
  readonly creatingWorkspace = signal(false);

  readonly isLoading = computed(() => this.membershipService.state().status === 'loading');
  readonly isError = computed(() => this.membershipService.state().status === 'error');
  readonly memberships = computed<readonly MyWorkspaceMembership[]>(() => {
    const state = this.membershipService.state();
    return state.status === 'ready' ? state.memberships : [];
  });

  onSelect(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const nextSlug = select.value;

    if (nextSlug === CREATE_OPTION_VALUE) {
      // The native <select> already shows "+ Create workspace…" as selected; put it back to the
      // current workspace immediately rather than waiting on change detection, since opening the
      // dialog doesn't itself change currentSlug().
      select.value = this.currentSlug() ?? '';
      this.creatingWorkspace.set(true);
      return;
    }

    if (nextSlug && nextSlug !== this.currentSlug()) {
      void this.router.navigate(['/', nextSlug, 'dashboard']);
    }
  }

  async onWorkspaceCreated(workspaceSlug: string): Promise<void> {
    this.creatingWorkspace.set(false);
    await this.router.navigate(['/', workspaceSlug, 'dashboard']);
  }
}
