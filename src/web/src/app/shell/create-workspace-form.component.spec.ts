import { TestBed } from '@angular/core/testing';

import { CreateWorkspaceFormComponent } from './create-workspace-form.component';
import { CreateWorkspaceOutcome, WorkspaceMembershipService } from '../services/workspace-membership.service';

describe('CreateWorkspaceFormComponent', () => {
  let createSpy: jasmine.Spy<(name: string) => Promise<CreateWorkspaceOutcome>>;
  let createdSpy: jasmine.Spy;

  async function createFixture() {
    TestBed.resetTestingModule();
    createSpy = jasmine.createSpy('create').and.resolveTo({ status: 'success', workspaceSlug: 'new-workspace' } satisfies CreateWorkspaceOutcome);

    await TestBed.configureTestingModule({
      imports: [CreateWorkspaceFormComponent],
      providers: [{ provide: WorkspaceMembershipService, useValue: { create: createSpy } }],
    }).compileComponents();

    const fixture = TestBed.createComponent(CreateWorkspaceFormComponent);
    createdSpy = jasmine.createSpy('created');
    fixture.componentInstance.created.subscribe(createdSpy);
    fixture.detectChanges();
    return fixture;
  }

  it('renders a workspace name field', async () => {
    const fixture = await createFixture();
    expect(fixture.nativeElement.querySelector('input[name="workspaceName"]')).toBeTruthy();
  });

  it('creates a workspace with the entered name and emits created with the new slug', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.workspaceName.set('Cozy Fall Recipes');

    await fixture.componentInstance.submit();

    expect(createSpy).toHaveBeenCalledWith('Cozy Fall Recipes');
    expect(createdSpy).toHaveBeenCalledWith('new-workspace');
  });

  it('shows a validation error and does not emit created on an invalid name', async () => {
    const fixture = await createFixture();
    createSpy.and.resolveTo({ status: 'invalid', message: 'Enter a workspace name.' } satisfies CreateWorkspaceOutcome);

    await fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(createdSpy).not.toHaveBeenCalled();
    expect(fixture.componentInstance.createError()).toBe('Enter a workspace name.');
    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent).toContain('Enter a workspace name.');
  });

  it('shows a generic error when the server is unreachable', async () => {
    const fixture = await createFixture();
    createSpy.and.resolveTo({ status: 'unavailable' } satisfies CreateWorkspaceOutcome);

    await fixture.componentInstance.submit();
    fixture.detectChanges();

    expect(fixture.componentInstance.createError()).toContain("couldn't reach the server");
  });

  it('ignores a second submit() call fired while the first is still in flight', async () => {
    const fixture = await createFixture();
    let resolveCreate!: (outcome: CreateWorkspaceOutcome) => void;
    createSpy.and.returnValue(new Promise((resolve) => (resolveCreate = resolve)));

    const first = fixture.componentInstance.submit();
    const second = fixture.componentInstance.submit();

    resolveCreate({ status: 'success', workspaceSlug: 'new-workspace' });
    await Promise.all([first, second]);

    expect(createSpy).toHaveBeenCalledTimes(1);
  });

  it('uses a unique field id per instance', async () => {
    const first = await createFixture();
    const second = await createFixture();

    const firstInput = first.nativeElement.querySelector('input[name="workspaceName"]') as HTMLInputElement;
    const secondInput = second.nativeElement.querySelector('input[name="workspaceName"]') as HTMLInputElement;
    expect(firstInput.id).not.toBe(secondInput.id);
  });
});
