import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { CreateWorkspaceOutcome, WorkspaceMembershipService } from '../services/workspace-membership.service';
import { WorkspaceGateComponent } from './workspace-gate.component';

describe('WorkspaceGateComponent', () => {
  let navigateSpy: jasmine.Spy;
  let ensureLoadedSpy: jasmine.Spy;
  let loadSpy: jasmine.Spy;
  let createSpy: jasmine.Spy<(name: string) => Promise<CreateWorkspaceOutcome>>;
  let state: { status: string; memberships?: unknown[] };

  async function createFixture() {
    navigateSpy = jasmine.createSpy('navigate').and.resolveTo(true);
    ensureLoadedSpy = jasmine.createSpy('ensureLoaded').and.callFake(async () => {});
    loadSpy = jasmine.createSpy('load').and.callFake(async () => {});
    createSpy = jasmine.createSpy('create').and.resolveTo({ status: 'success', workspaceSlug: 'new-workspace' } satisfies CreateWorkspaceOutcome);

    await TestBed.configureTestingModule({
      imports: [WorkspaceGateComponent],
      providers: [
        {
          provide: WorkspaceMembershipService,
          useValue: { state: () => state, ensureLoaded: ensureLoadedSpy, load: loadSpy, create: createSpy },
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

  it('shows a create-workspace form instead of redirecting when there are no memberships', async () => {
    state = { status: 'ready', memberships: [] };
    const fixture = await createFixture();

    expect(navigateSpy).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Create your first workspace');
    expect(fixture.nativeElement.querySelector('input[name="workspaceName"]')).toBeTruthy();
  });

  it('navigates into the new workspace when the create form reports success', async () => {
    state = { status: 'ready', memberships: [] };
    const fixture = await createFixture();

    await fixture.componentInstance.onCreated('new-workspace');

    expect(navigateSpy).toHaveBeenCalledWith(['/', 'new-workspace', 'dashboard']);
  });

  it('keeps the create form mounted once the membership state reports the new workspace', async () => {
    // The real service's state() is a signal and create() refreshes it from the server before it
    // resolves. A branch that kept reading the membership count would therefore tear this form down
    // in the same change-detection pass its (created) output fires in: the output would be dropped
    // against a destroyed component (NG0953), nothing would navigate, and the creator would be left
    // on a blank page with the new workspace reachable only from the switcher.
    const liveState = signal<{ status: string; memberships?: unknown[] }>({ status: 'ready', memberships: [] });
    navigateSpy = jasmine.createSpy('navigate').and.resolveTo(true);
    ensureLoadedSpy = jasmine.createSpy('ensureLoaded').and.callFake(async () => {});
    loadSpy = jasmine.createSpy('load').and.callFake(async () => {});
    createSpy = jasmine.createSpy('create').and.resolveTo({ status: 'success', workspaceSlug: 'new-workspace' } satisfies CreateWorkspaceOutcome);

    await TestBed.configureTestingModule({
      imports: [WorkspaceGateComponent],
      providers: [
        { provide: WorkspaceMembershipService, useValue: { state: liveState, ensureLoaded: ensureLoadedSpy, load: loadSpy, create: createSpy } },
        { provide: Router, useValue: { navigate: navigateSpy } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(WorkspaceGateComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('input[name="workspaceName"]')).toBeTruthy();

    liveState.set({
      status: 'ready',
      memberships: [{ workspaceId: 'w9', workspaceSlug: 'new-workspace', workspaceName: 'New', membershipId: 'm9', role: 'Owner', status: 'Active' }],
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('input[name="workspaceName"]')).toBeTruthy();
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
    createSpy = jasmine.createSpy('create').and.resolveTo({ status: 'success', workspaceSlug: 'new-workspace' } satisfies CreateWorkspaceOutcome);

    await TestBed.configureTestingModule({
      imports: [WorkspaceGateComponent],
      providers: [
        { provide: WorkspaceMembershipService, useValue: { state: () => state, ensureLoaded: ensureLoadedSpy, load: loadSpy, create: createSpy } },
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
