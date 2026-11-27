import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import {
  CreateWorkspaceOutcome,
  MyMembershipsState,
  WorkspaceMembershipService,
} from '../services/workspace-membership.service';
import { WorkspaceSwitcherComponent } from './workspace-switcher.component';

describe('WorkspaceSwitcherComponent', () => {
  let navigateSpy: jasmine.Spy;
  let createSpy: jasmine.Spy<(name: string) => Promise<CreateWorkspaceOutcome>>;
  let stateSignal: ReturnType<typeof signal<MyMembershipsState>>;

  const oneMembership: MyMembershipsState = {
    status: 'ready',
    memberships: [
      {
        workspaceId: 'w1',
        workspaceSlug: 'cozy-fall',
        workspaceName: 'Cozy Fall',
        membershipId: 'm1',
        role: 'Owner',
        status: 'Active',
      },
    ],
  };

  const twoMemberships: MyMembershipsState = {
    status: 'ready',
    memberships: [
      ...oneMembership.memberships,
      {
        workspaceId: 'w2',
        workspaceSlug: 'weeknight',
        workspaceName: 'Weeknight',
        membershipId: 'm2',
        role: 'Viewer',
        status: 'Active',
      },
    ],
  };

  async function createFixture(initialState: MyMembershipsState, currentSlug = 'cozy-fall') {
    stateSignal = signal(initialState);
    navigateSpy = jasmine.createSpy('navigate').and.resolveTo(true);
    createSpy = jasmine
      .createSpy('create')
      .and.resolveTo({ status: 'success', workspaceSlug: 'new-workspace' } satisfies CreateWorkspaceOutcome);

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

  type Fixture = ComponentFixture<WorkspaceSwitcherComponent>;

  function trigger(fixture: Fixture): HTMLButtonElement {
    return fixture.nativeElement.querySelector('button[aria-haspopup="menu"]');
  }

  function rows(fixture: Fixture): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('[role="menu"] button'));
  }

  async function openMenu(fixture: Fixture) {
    trigger(fixture).click();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('shows a loading status while memberships load', async () => {
    const fixture = await createFixture({ status: 'loading' });
    expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain('Loading');

    // Said once, offscreen; what shows is a line the switcher's width (AU.7).
    expect(fixture.nativeElement.querySelector('[role="status"]')?.classList).toContain('cp-sr-only');
    expect(fixture.nativeElement.querySelector('.loading[aria-busy="true"] cp-skeleton')).not.toBeNull();
  });

  it('keeps that status, emptied, once the memberships are read', async () => {
    const fixture = await createFixture({ status: 'ready', memberships: [] });
    expect(fixture.nativeElement.querySelector('.cp-sr-only[role="status"]')?.textContent).toBe('');
    expect(fixture.nativeElement.querySelector('cp-skeleton')).toBeNull();
  });

  it('shows an error status when memberships fail to load', async () => {
    const fixture = await createFixture({ status: 'error' });
    expect(fixture.nativeElement.textContent).toContain('unavailable');
  });

  it('shows the current workspace on a closed trigger and no menu', async () => {
    const fixture = await createFixture(twoMemberships);

    expect(trigger(fixture).textContent).toContain('Cozy Fall');
    expect(trigger(fixture).getAttribute('aria-expanded')).toBe('false');
    expect(fixture.nativeElement.querySelector('[role="menu"]')).toBeNull();
    // A native select's open list is painted by the OS and cannot be themed; this must not come back.
    expect(fixture.nativeElement.querySelector('select')).toBeNull();
  });

  it('lists every active membership, the current one checked, then a create action', async () => {
    const fixture = await createFixture(twoMemberships);
    await openMenu(fixture);

    const items = rows(fixture);
    expect(items.length).toBe(3); // 2 memberships + "Create workspace…"
    expect(items.map((r) => r.getAttribute('role'))).toEqual(['menuitemradio', 'menuitemradio', 'menuitem']);
    expect(items.map((r) => r.getAttribute('aria-checked'))).toEqual(['true', 'false', null]);
    expect(trigger(fixture).getAttribute('aria-expanded')).toBe('true');
    // Focus lands on the current workspace so the arrow keys start from where the creator is.
    expect(document.activeElement).toBe(items[0]);
  });

  it('navigates to the new slug + dashboard on selection, never sending WorkspaceId anywhere', async () => {
    const fixture = await createFixture(twoMemberships);
    await openMenu(fixture);

    rows(fixture)[1].click();
    fixture.detectChanges();

    expect(navigateSpy).toHaveBeenCalledWith(['/', 'weeknight', 'dashboard']);
    expect(fixture.nativeElement.querySelector('[role="menu"]')).toBeNull();
  });

  it('shows a fallback message instead of rendering nothing when ready with zero memberships', async () => {
    const fixture = await createFixture({ status: 'ready', memberships: [] });
    expect(fixture.nativeElement.textContent).toContain('No workspaces');
    expect(trigger(fixture)).toBeFalsy();
  });

  it('does not navigate when re-selecting the already-current workspace', async () => {
    const fixture = await createFixture(twoMemberships);
    await openMenu(fixture);

    rows(fixture)[0].click();
    fixture.detectChanges();

    expect(navigateSpy).not.toHaveBeenCalled();
  });

  describe('create workspace', () => {
    async function chooseCreate(fixture: Fixture) {
      await openMenu(fixture);
      rows(fixture).at(-1)!.click();
      fixture.detectChanges();
    }

    it('does not navigate when the create action is chosen, and keeps the current workspace shown', async () => {
      const fixture = await createFixture(oneMembership);

      await chooseCreate(fixture);

      expect(navigateSpy).not.toHaveBeenCalled();
      expect(trigger(fixture).textContent).toContain('Cozy Fall');
    });

    it('opens the create-workspace dialog when the create action is chosen', async () => {
      const fixture = await createFixture(oneMembership);

      await chooseCreate(fixture);

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
