import { ChangeDetectionStrategy, Component, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpFieldComponent } from '@creator-pantry/ui';

import { WorkspaceMembershipService } from '../services/workspace-membership.service';

/**
 * The workspace-creation form itself, with no opinion on where it's shown (the empty first-use screen
 * projects it into a `cp-empty-state`; the workspace switcher projects it into a `cp-dialog`). Becomes
 * the caller's Owner atomically (POST /api/v1/workspaces) and emits the new slug on success; the caller
 * decides what happens next (navigate, close a dialog, both).
 */
@Component({
  selector: 'cp-create-workspace-form',
  standalone: true,
  imports: [FormsModule, CpFieldComponent, CpButtonComponent],
  template: `
    <form class="create-workspace-form" (submit)="$event.preventDefault(); submit()">
      @if (createError()) {
        <p class="banner banner--error" role="alert">{{ createError() }}</p>
      }

      <cp-field label="Workspace name" [forId]="fieldId" [required]="true">
        <input
          [id]="fieldId"
          type="text"
          required
          [attr.aria-invalid]="createError() ? 'true' : null"
          [ngModel]="workspaceName()"
          (ngModelChange)="workspaceName.set($event)"
          name="workspaceName"
        />
      </cp-field>

      <button cpButton type="submit" [fullWidth]="true" [disabled]="creating()" [attr.aria-busy]="creating()">
        {{ creating() ? 'Creating…' : 'Create workspace' }}
      </button>
    </form>
  `,
  styles: [`
    .create-workspace-form { display: grid; gap: var(--cp-space-3); width: min(22rem, 100%); text-align: left; }
    .banner { margin: 0; padding: var(--cp-space-2) var(--cp-space-3); border-radius: var(--cp-radius-md); font-size: var(--cp-font-size-sm); }
    .banner--error { background: color-mix(in srgb, var(--cp-danger) 14%, var(--cp-surface)); color: var(--cp-danger); }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CreateWorkspaceFormComponent {
  private static nextId = 0;

  private readonly memberships = inject(WorkspaceMembershipService);

  readonly fieldId = `workspace-name-${CreateWorkspaceFormComponent.nextId++}`;

  readonly workspaceName = signal('');
  readonly creating = signal(false);
  readonly createError = signal('');

  /** Emits the new workspace's slug on success. */
  readonly created = output<string>();

  async submit(): Promise<void> {
    if (this.creating()) return;
    this.creating.set(true);
    this.createError.set('');

    const outcome = await this.memberships.create(this.workspaceName());
    this.creating.set(false);

    switch (outcome.status) {
      case 'success':
        this.created.emit(outcome.workspaceSlug);
        return;
      case 'invalid':
        this.createError.set(outcome.message);
        return;
      case 'unavailable':
        this.createError.set("We couldn't reach the server. Check your connection and try again.");
        return;
    }
  }
}
