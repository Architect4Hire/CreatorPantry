import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { MyMembershipsState, WorkspaceMembershipService } from '../services/workspace-membership.service';
import { WorkspaceSwitcherComponent } from './workspace-switcher.component';

describe('WorkspaceSwitcherComponent', () => {
  let navigateSpy: jasmine.Spy;
  let stateSignal: ReturnType<typeof signal<MyMembershipsState>>;

  async function createFixture(initialState: MyMembershipsState, currentSlug = 'cozy-fall') {
    stateSignal = signal(initialState);
    navigateSpy = jasmine.createSpy('navigate').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [WorkspaceSwitcherComponent],
      providers: [
        { provide: WorkspaceMembershipService, useValue: { state: stateSignal } },
        { provide: Router, useValue: { navigate: navigateSpy } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(WorkspaceSwitcherComponent);
    fixture.componentRef.setInput('currentSlug', currentSlug);
    fixture.componentRef.setInput('currentSection', 'recipes');
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
    expect(options.length).toBe(2);
    expect(fixture.nativeElement.querySelector('select').value).toBe('cozy-fall');
  });

  it('navigates to the new slug + current section on selection, never sending WorkspaceId anywhere', async () => {
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

    expect(navigateSpy).toHaveBeenCalledWith(['/', 'weeknight', 'recipes']);
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
});
