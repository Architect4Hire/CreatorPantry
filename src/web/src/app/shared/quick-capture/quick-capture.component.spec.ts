import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ConfirmService } from '../../core/confirm.service';
import { NetworkStatusService } from '../../core/network-status.service';
import { CaptureWorkTaskRequest, WorkTask } from '../../models/work-task.models';
import { CaptureWorkTaskOutcome, PlanningDatesOutcome, WorkTaskService } from '../../services/work-task.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { QuickCaptureComponent } from './quick-capture.component';
import { QuickCaptureService } from './quick-capture.service';

const RECIPE = '6f1c2a4e-8b1d-4c3e-9f2a-1b2c3d4e5f60';

function task(overrides: Partial<WorkTask> = {}): WorkTask {
  return {
    id: 't-1',
    title: 'Shoot the galette',
    description: null,
    status: 'Backlog',
    priority: 'Normal',
    scheduledDate: null,
    planningWeekStart: '2038-10-11',
    dueDate: null,
    estimateMinutes: null,
    owner: { membershipId: 'm-1', displayName: 'Sam' },
    assignee: { membershipId: 'm-1', displayName: 'Sam' },
    links: { workflowRunId: null, workflowStepKey: null, workflowRunRoute: null, recipeId: null, contentCardId: null, mediaAssetId: null },
    labels: [],
    createdAt: '2038-10-14T17:00:00Z',
    updatedAt: '2038-10-14T17:00:00Z',
    completedAt: null,
    concurrencyToken: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

describe('QuickCaptureComponent', () => {
  let fixture: ComponentFixture<QuickCaptureComponent>;
  let capture: jasmine.Spy<(slug: string, request: CaptureWorkTaskRequest, key: string) => Promise<CaptureWorkTaskOutcome>>;
  let planningDates: jasmine.Spy<(slug: string) => Promise<PlanningDatesOutcome>>;
  let confirm: jasmine.Spy;
  let online: ReturnType<typeof signal<boolean>>;

  async function create(link: unknown = null): Promise<void> {
    capture = jasmine.createSpy('capture').and.callFake(async (_slug, request) => ({
      status: 'captured',
      task: task({ title: request.title, scheduledDate: request.scheduledDate ?? null, planningWeekStart: request.planningWeekStart ?? '2038-10-11' }),
      replayed: false,
    }));
    planningDates = jasmine.createSpy('planningDates').and.resolveTo({ status: 'found', dates: { today: '2038-10-14', weekStart: '2038-10-11' } });
    confirm = jasmine.createSpy('confirm').and.resolveTo(true);
    online = signal(true);

    await TestBed.configureTestingModule({
      imports: [QuickCaptureComponent],
      providers: [
        { provide: WorkTaskService, useValue: { capture, planningDates } },
        {
          provide: WorkspaceTagService,
          useValue: { list: jasmine.createSpy('list').and.resolveTo({ status: 'found', tags: [{ id: 'tag-1', name: 'Weeknight' }] }) },
        },
        { provide: ConfirmService, useValue: { confirm } },
        { provide: NetworkStatusService, useValue: { online } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(QuickCaptureComponent);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('link', link);
    fixture.detectChanges();
  }

  const host = () => fixture.nativeElement as HTMLElement;
  const $ = <T extends Element>(selector: string) => host().querySelector<T>(selector);

  async function settle(): Promise<void> {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function openDialog(): Promise<void> {
    fixture.componentInstance.open();
    await settle();
  }

  const saveButton = () =>
    [...host().querySelectorAll<HTMLButtonElement>('button')].find((button) => /^(Save|Saving…)$/.test(button.textContent!.trim()))!;

  function escapeFrom(element: Element): void {
    element.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
  }

  function type(value: string): void {
    const input = $<HTMLInputElement>('#cp-quick-capture-title-input')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function submit(): Promise<void> {
    $<HTMLFormElement>('form.capture')!.requestSubmit();
    await settle();
  }

  async function expand(): Promise<void> {
    $<HTMLButtonElement>('.disclosure')!.click();
    await settle();
  }

  async function choose(id: string): Promise<void> {
    $<HTMLInputElement>(`#${id}`)!.click();
    await settle();
  }

  // The dialog's own message regions — the label's list keeps a result count in a status region of its own.
  const statusText = () => $('.messages [role="status"]')!.textContent!.trim();
  const alertText = () => $('.messages [role="alert"]')!.textContent!.trim();

  // ---- One field ----

  it('opens one field with focus in it and nothing else asked for', async () => {
    await create();
    await openDialog();

    expect($('[role="dialog"]')).not.toBeNull();
    expect(document.activeElement?.id).toBe('cp-quick-capture-title-input');
    expect($<HTMLElement>('#cp-quick-capture-details')!.hidden).toBeTrue();
    expect($('.disclosure')!.getAttribute('aria-expanded')).toBe('false');
  });

  it('saves a title alone as exactly that, says so politely, and leaves focus where it was', async () => {
    await create();
    await openDialog();
    type('  Shoot the galette  ');
    const input = $<HTMLInputElement>('#cp-quick-capture-title-input')!;
    input.focus();

    await submit();

    expect(capture).toHaveBeenCalledOnceWith('sams-kitchen', { title: 'Shoot the galette' }, jasmine.any(String));
    expect(statusText()).toContain('Added “Shoot the galette”');
    expect(input.value).toBe('');
    expect(document.activeElement).toBe(input);
    expect($('[role="dialog"]')).not.toBeNull();
  });

  it('asks for a title rather than saving an empty one', async () => {
    await create();
    await openDialog();

    await submit();

    expect(capture).not.toHaveBeenCalled();
    expect(host().textContent).toContain('Write what needs doing.');
    expect($('#cp-quick-capture-title-input')!.getAttribute('aria-invalid')).toBe('true');
  });

  // ---- More details ----

  it('opens the details in the same dialog, all optional, and sends only what was chosen', async () => {
    await create({ field: 'recipeId', id: RECIPE, label: 'Link it to the recipe you’re viewing' });
    await openDialog();
    await expand();

    expect($('.disclosure')!.getAttribute('aria-expanded')).toBe('true');
    expect(host().querySelectorAll('[role="dialog"]').length).toBe(1);

    type('Shoot the galette');
    await choose('cp-quick-capture-when-tomorrow');
    await choose('cp-quick-capture-priority-High');

    const estimate = $<HTMLSelectElement>('#cp-quick-capture-estimate')!;
    estimate.value = '45';
    estimate.dispatchEvent(new Event('change'));

    $<HTMLInputElement>('#cp-quick-capture-link')!.click();
    await settle();

    await submit();

    expect(capture.calls.mostRecent().args[1]).toEqual({
      title: 'Shoot the galette',
      priority: 'High',
      scheduledDate: '2038-10-15',
      estimateMinutes: 45,
      recipeId: RECIPE,
    });
    expect(statusText()).toContain('tomorrow');
  });

  it('plans this week and next week by the server’s own week, not the browser’s', async () => {
    await create();
    await openDialog();
    await expand();

    type('Plan the shoot');
    await choose('cp-quick-capture-when-next-week');
    await submit();

    expect(capture.calls.mostRecent().args[1]).toEqual({ title: 'Plan the shoot', planningWeekStart: '2038-10-18' });
    expect(statusText()).toContain('next week');
  });

  it('offers a label from the workspace’s own tags', async () => {
    await create();
    await openDialog();
    await expand();

    const label = $<HTMLInputElement>('#cp-quick-capture-label')!;
    label.value = 'week';
    label.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    label.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();
    label.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await settle();

    type('Labelled');
    await submit();

    expect(capture.calls.mostRecent().args[1]).toEqual({ title: 'Labelled', labelIds: ['tag-1'] });
  });

  it('says how many details a collapsed "More details" still holds', async () => {
    await create();
    await openDialog();
    await expand();
    await choose('cp-quick-capture-priority-Low');
    await expand();

    expect($('.disclosure')!.textContent).toContain('(1 set)');
  });

  it('offers no linking on a page about no one record', async () => {
    await create(null);
    await openDialog();
    await expand();

    expect($('#cp-quick-capture-link')).toBeNull();
  });

  it('hides today and the weeks where the workspace keeps no calendar, and says why in a status', async () => {
    await create();
    planningDates.and.resolveTo({ status: 'no_time_zone' });
    await openDialog();
    await expand();

    expect($('#cp-quick-capture-when-today')).toBeNull();
    expect($('#cp-quick-capture-when-pick')).not.toBeNull();
    expect($('#cp-quick-capture-details [role="status"]')!.textContent).toContain('time zone');
  });

  // ---- When it does not save ----

  it('keeps the title and every detail when a save fails, and retries the same request with the same key', async () => {
    await create();
    capture.and.resolveTo({ status: 'unavailable', offline: false });
    await openDialog();
    await expand();
    type('Shoot the galette');
    await choose('cp-quick-capture-priority-High');

    await submit();

    expect(alertText()).toContain('your task is still here');
    expect($<HTMLInputElement>('#cp-quick-capture-title-input')!.value).toBe('Shoot the galette');
    expect($<HTMLInputElement>('#cp-quick-capture-priority-High')!.checked).toBeTrue();

    await submit();
    expect(capture.calls.argsFor(1)[2]).toBe(capture.calls.argsFor(0)[2]);

    // A changed request is a new request, and gets a new key.
    type('Shoot the plum galette');
    await submit();
    expect(capture.calls.argsFor(2)[2]).not.toBe(capture.calls.argsFor(0)[2]);
  });

  it('puts a field the server refused under that field', async () => {
    await create();
    capture.and.resolveTo({ status: 'invalid', fieldErrors: { title: ['A title can be at most 200 characters.'] } });
    await openDialog();
    type('Shoot the galette');

    await submit();

    expect(host().textContent).toContain('A title can be at most 200 characters.');
    expect(document.activeElement?.id).toBe('cp-quick-capture-title-input');
  });

  it('takes off a link the server would not take, and says so', async () => {
    await create({ field: 'recipeId', id: RECIPE, label: 'Link it to the recipe you’re viewing' });
    capture.and.resolveTo({ status: 'link_refused', fields: ['recipeId'] });
    await openDialog();
    await expand();
    type('Linked');
    $<HTMLInputElement>('#cp-quick-capture-link')!.click();
    await settle();

    await submit();

    expect($<HTMLInputElement>('#cp-quick-capture-link')!.checked).toBeFalse();
    expect(alertText()).toContain('can’t be linked');
  });

  it('will not save while offline, by Enter or by Save, and says why on the button', async () => {
    await create();
    await openDialog();
    online.set(false);
    fixture.detectChanges();
    type('Offline thought');

    await submit();
    saveButton().click();
    await settle();

    expect(capture).not.toHaveBeenCalled();
    expect(saveButton().getAttribute('aria-disabled')).toBe('true');
    expect(saveButton().getAttribute('aria-describedby')).toBe('cp-quick-capture-offline');
    expect($('#cp-quick-capture-offline')!.textContent).toContain('offline');
    expect($<HTMLInputElement>('#cp-quick-capture-title-input')!.value).toBe('Offline thought');
  });

  it('keeps focus on Save, inside the dialog, through a save made from it', async () => {
    await create();
    await openDialog();
    type('From the button');
    saveButton().focus();

    saveButton().click();
    await settle();

    expect(capture).toHaveBeenCalled();
    expect(document.activeElement).toBe(saveButton());
    expect(saveButton().disabled).toBeFalse();

    // And Escape from there still closes it.
    escapeFrom(saveButton());
    await settle();
    expect($('[role="dialog"]')).toBeNull();
  });

  it('closes on Escape from anywhere in it, the footer and the close control included', async () => {
    await create();
    await openDialog();
    escapeFrom($('[aria-label="Close dialog"]')!);
    await settle();
    expect($('[role="dialog"]')).toBeNull();

    await openDialog();
    escapeFrom(saveButton());
    await settle();
    expect($('[role="dialog"]')).toBeNull();
  });

  it('asks before losing a label that was typed but not chosen, and will not save without it', async () => {
    await create();
    await openDialog();
    await expand();
    type('Half labelled');

    const label = $<HTMLInputElement>('#cp-quick-capture-label')!;
    label.value = 'weekn';
    label.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    await submit();
    expect(capture).not.toHaveBeenCalled();
    expect(host().textContent).toContain('Choose a label from the list, or clear it.');
    expect(document.activeElement).toBe(label);

    // Escape in the label box is the box's own — it closes its list — so the question comes from elsewhere.
    confirm.and.resolveTo(false);
    type('');
    escapeFrom($('#cp-quick-capture-title-input')!);
    await settle();
    expect(confirm).toHaveBeenCalled();
  });

  it('puts a missing day beside the day field and moves focus to it', async () => {
    await create();
    await openDialog();
    await expand();
    type('On a day');
    await choose('cp-quick-capture-when-pick');

    await submit();

    expect(capture).not.toHaveBeenCalled();
    expect(host().textContent).toContain('Pick the day, or choose another option under When.');
    expect(document.activeElement?.id).toBe('cp-quick-capture-day');
  });

  it('announces that the calendar could not be read, offers to try again, and still offers a day', async () => {
    await create();
    planningDates.and.resolveTo({ status: 'unavailable' });
    await openDialog();
    await expand();

    const status = $('#cp-quick-capture-details [role="status"]')!;
    expect(status.textContent).toContain('couldn’t be worked out');
    expect($('#cp-quick-capture-when-pick')).not.toBeNull();

    planningDates.and.resolveTo({ status: 'found', dates: { today: '2038-10-14', weekStart: '2038-10-11' } });
    [...status.querySelectorAll('button')].find((button) => button.textContent!.includes('try again'))!.click();
    await settle();
    expect($('#cp-quick-capture-when-today')).not.toBeNull();
  });

  // ---- Closing ----

  it('closes at once when nothing was typed', async () => {
    await create();
    await openDialog();

    escapeFrom($('#cp-quick-capture-title-input')!);
    await settle();

    expect(confirm).not.toHaveBeenCalled();
    expect($('[role="dialog"]')).toBeNull();
  });

  it('asks before discarding what was typed, and keeps it when the answer is to keep writing', async () => {
    await create();
    await openDialog();
    type('Half a thought');
    confirm.and.resolveTo(false);

    escapeFrom($('#cp-quick-capture-title-input')!);
    await settle();

    expect(confirm).toHaveBeenCalled();
    expect($('[role="dialog"]')).not.toBeNull();
    expect($<HTMLInputElement>('#cp-quick-capture-title-input')!.value).toBe('Half a thought');

    confirm.and.resolveTo(true);
    $<HTMLButtonElement>('[aria-label="Close dialog"]')!.click();
    await settle();
    expect($('[role="dialog"]')).toBeNull();
  });

  // ---- Opened by a page (13A.32) ----

  it('starts on Today for the day the page names, and saves to that day without opening the details', async () => {
    await create();
    fixture.componentInstance.open({ today: '2038-10-14' });
    await settle();

    type('Prep the dough');
    await submit();

    expect(capture.calls.mostRecent().args[1]).toEqual({ title: 'Prep the dough', scheduledDate: '2038-10-14' });
    expect(planningDates).not.toHaveBeenCalled();
    expect(host().textContent).toContain('Added “Prep the dough” to today.');

    // Ready for the next one, still on Today.
    await expand();
    expect($<HTMLInputElement>('#cp-quick-capture-when-today')!.checked).toBeTrue();
  });

  it('tells the page it made a task', async () => {
    await create();
    const heard: string[] = [];
    TestBed.inject(QuickCaptureService).captured$.subscribe((captured) => heard.push(captured.title));

    await openDialog();
    type('Prep the dough');
    await submit();

    expect(heard).toEqual(['Prep the dough']);
  });

  it('closes without asking when only the page’s own starting point is set', async () => {
    await create();
    fixture.componentInstance.open({ today: '2038-10-14' });
    await settle();

    escapeFrom($('#cp-quick-capture-title-input')!);
    await settle();

    expect(confirm).not.toHaveBeenCalled();
    expect($('[role="dialog"]')).toBeNull();
  });

  it('opened from the top bar after a page, starts on No day yet again', async () => {
    await create();
    fixture.componentInstance.open({ today: '2038-10-14' });
    await settle();
    escapeFrom($('#cp-quick-capture-title-input')!);
    await settle();

    await openDialog();
    type('Someday');
    await submit();

    expect(capture.calls.mostRecent().args[1]).toEqual({ title: 'Someday' });
  });

  it('starts on the week a page names, and saves to that week’s backlog (13A.33)', async () => {
    await create();
    fixture.componentInstance.open({ weekStart: '2038-10-18' });
    await settle();

    type('Plan the bake sale');
    await submit();

    expect(capture.calls.mostRecent().args[1]).toEqual({ title: 'Plan the bake sale', planningWeekStart: '2038-10-18' });

    await expand();
    expect($<HTMLInputElement>('#cp-quick-capture-when-week-of')!.checked).toBeTrue();
    expect(host().textContent).toContain('Week of');
  });

  it('names every control it offers', async () => {
    await create({ field: 'recipeId', id: RECIPE, label: 'Link it to the recipe you’re viewing' });
    await openDialog();
    await expand();

    for (const id of ['cp-quick-capture-title-input', 'cp-quick-capture-estimate', 'cp-quick-capture-label', 'cp-quick-capture-link']) {
      const control = $<HTMLElement>(`#${id}`)!;
      const labelled = host().querySelector(`label[for="${id}"]`) ?? control.closest('label');
      expect(labelled?.textContent?.trim()).withContext(id).toBeTruthy();
    }
    expect($('[role="dialog"]')!.getAttribute('aria-labelledby')).toBe('cp-quick-capture-title');
  });
});
