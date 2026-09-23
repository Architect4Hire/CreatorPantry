import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { WorkspaceMembershipService } from '../services/workspace-membership.service';
import { WorkspaceGateComponent } from './workspace-gate.component';

describe('WorkspaceGateComponent', () => {
  let navigateSpy: jasmine.Spy;
  let ensureLoadedSpy: jasmine.Spy;
  let loadSpy: jasmine.Spy;
  let state: { status: string; memberships?: unknown[] };

  async function createFixture() {
    navigateSpy = jasmine.createSpy('navigate').and.resolveTo(true);
    ensureLoadedSpy = jasmine.createSpy('ensureLoaded').and.callFake(async () => {});
    loadSpy = jasmine.createSpy('load').and.callFake(async () => {});

    await TestBed.configureTestingModule({
      imports: [WorkspaceGateComponent],
      providers: [
        {
          provide: WorkspaceMembershipService,
          useValue: { state: () => state, ensureLoaded: ensureLoadedSpy, load: loadSpy },
        },
        { provide: Router, useValue: { navigate: navigateSpy } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(WorkspaceGateComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('redirects to the first membership\'s workspace slug + dashboard', async () => {
    state = {
      status: 'ready',
      memberships: [{ workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' }],
    };
    await createFixture();

    expect(navigateSpy).toHaveBeenCalledWith(['/', 'cozy-fall', 'dashboard']);
  });

  it('shows a first-use empty state instead of redirecting when there are no memberships', async () => {
    state = { status: 'ready', memberships: [] };
    const fixture = await createFixture();

    expect(navigateSpy).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('No workspace yet');
  });

  it('shows an error state with a working retry that calls load(), not ensureLoaded()', async () => {
    state = { status: 'error' };
    const fixture = await createFixture();

    const retryButton = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(retryButton).toBeTruthy();

    retryButton.click();
    expect(loadSpy).toHaveBeenCalled();
  });

  it('does not navigate if the component is destroyed before the pending load resolves', async () => {
    state = { status: 'loading' };
    let resolveEnsureLoaded!: () => void;
    ensureLoadedSpy = jasmine.createSpy('ensureLoaded').and.returnValue(new Promise<void>((resolve) => (resolveEnsureLoaded = resolve)));
    navigateSpy = jasmine.createSpy('navigate').and.resolveTo(true);
    loadSpy = jasmine.createSpy('load').and.callFake(async () => {});

    await TestBed.configureTestingModule({
      imports: [WorkspaceGateComponent],
      providers: [
        { provide: WorkspaceMembershipService, useValue: { state: () => state, ensureLoaded: ensureLoadedSpy, load: loadSpy } },
        { provide: Router, useValue: { navigate: navigateSpy } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(WorkspaceGateComponent);
    fixture.detectChanges();

    fixture.destroy();
    state = {
      status: 'ready',
      memberships: [{ workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active' }],
    };
    resolveEnsureLoaded();
    await Promise.resolve().then(() => Promise.resolve());

    expect(navigateSpy).not.toHaveBeenCalled();
  });
});
