import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { CreateWorkspaceOutcome, MyMembershipsState, WorkspaceMembershipService } from '../services/workspace-membership.service';
import { WorkspaceSwitcherComponent } from './workspace-switcher.component';

describe('WorkspaceSwitcherComponent', () => {
  let navigateSpy: jasmine.Spy;
  let createSpy: jasmine.Spy<(name: string) => Promise<CreateWorkspaceOutcome>>;
  let stateSignal: ReturnType<typeof signal<MyMembershipsState>>;

  async function createFixture(initialState: MyMembershipsState, currentSlug = 'cozy-fall') {
    stateSignal = signal(initialState);
    navigateSpy = jasmine.createSpy('navigate').and.resolveTo(true);
    createSpy = jasmine.createSpy('create').and.resolveTo({ status: 'success', workspaceSlug: 'new-workspace' } satisfies CreateWorkspaceOutcome);

    await TestBed.configureTestingModule({
      imports: [WorkspaceSwitcherComponent],
      providers: [
        { provide: WorkspaceMembershipService, useValue: { state: stateSignal, create: createSpy } },
        { provide: Router, useValue: { navigate: navigateSpy } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(WorkspaceSwitcherComponent);
    fixture.componentRef.setInput('currentSlug', currentSlug);
    fixture.detectChanges();
    return fixture;
  }

  it('shows a loading status while memberships load', async () => {
    const fixture = await createFixture({ status: 'loading' });
    expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain('Loading');
  });

  it('shows an error status when memberships fail to load', async () => {
    const fixture = await createFixture({ status: 'error' });
    expect(fixture.nativeElement.textContent).toContain('unavailable');
  });

  it('lists every active membership as a select option', async () => {
    const fixture = await createFixture({
      status: 'ready',
      memberships: [
        { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' },
        { workspaceId: 'w2', workspaceSlug: 'weeknight', workspaceName: 'Weeknight', membershipId: 'm2', role: 'Viewer', status: 'Active' },
      ],
    });

    const options = fixture.nativeElement.querySelectorAll('option');
    expect(options.length).toBe(3); // 2 memberships + "Create workspace…"
    expect(fixture.nativeElement.querySelector('select').value).toBe('cozy-fall');
  });

  it('navigates to the new slug + dashboard on selection, never sending WorkspaceId anywhere', async () => {
    const fixture = await createFixture({
      status: 'ready',
      memberships: [
        { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' },
        { workspaceId: 'w2', workspaceSlug: 'weeknight', workspaceName: 'Weeknight', membershipId: 'm2', role: 'Viewer', status: 'Active' },
      ],
    });

    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    select.value = 'weeknight';
    select.dispatchEvent(new Event('change'));

    expect(navigateSpy).toHaveBeenCalledWith(['/', 'weeknight', 'dashboard']);
  });

  it('shows a fallback message instead of rendering nothing when ready with zero memberships', async () => {
    const fixture = await createFixture({ status: 'ready', memberships: [] });
    expect(fixture.nativeElement.textContent).toContain('No workspaces');
    expect(fixture.nativeElement.querySelector('select')).toBeFalsy();
  });

  it('does not navigate when re-selecting the already-current workspace', async () => {
    const fixture = await createFixture({
      status: 'ready',
      memberships: [{ workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' }],
    });

    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    select.value = 'cozy-fall';
    select.dispatchEvent(new Event('change'));

    expect(navigateSpy).not.toHaveBeenCalled();
  });

  describe('create workspace', () => {
    const oneMembership: MyMembershipsState = {
      status: 'ready',
      memberships: [{ workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' }],
    };

    it('does not navigate when the create option is selected, and resets the select back to the current workspace', async () => {
      const fixture = await createFixture(oneMembership);

      const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
      select.value = '__create__';
      select.dispatchEvent(new Event('change'));

      expect(navigateSpy).not.toHaveBeenCalled();
      expect(select.value).toBe('cozy-fall');
    });

    it('opens the create-workspace dialog when the create option is selected', async () => {
      const fixture = await createFixture(oneMembership);

      const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
      select.value = '__create__';
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('input[name="workspaceName"]')).toBeTruthy();
    });

    it('navigates into the new workspace and closes the dialog when the form reports success', async () => {
      const fixture = await createFixture(oneMembership);
      fixture.componentInstance.creatingWorkspace.set(true);
      fixture.detectChanges();

      await fixture.componentInstance.onWorkspaceCreated('new-workspace');
      fixture.detectChanges();

      expect(navigateSpy).toHaveBeenCalledWith(['/', 'new-workspace', 'dashboard']);
      expect(fixture.componentInstance.creatingWorkspace()).toBeFalse();
      expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeFalsy();
    });

    it('closes the dialog without navigating when dismissed', async () => {
      const fixture = await createFixture(oneMembership);
      fixture.componentInstance.creatingWorkspace.set(true);
      fixture.detectChanges();

      const closeButton = fixture.nativeElement.querySelector('[aria-label="Close dialog"]') as HTMLButtonElement;
      closeButton.click();
      fixture.detectChanges();

      expect(navigateSpy).not.toHaveBeenCalled();
      expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeFalsy();
    });
  });
});
