import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { DamAssetUtilization, damLocalDateValue } from '../../models/dam-asset.models';
import { DamAssetService, DamUseLogOutcome } from '../../services/dam-asset.service';
import { DamAssetLogUseComponent } from './dam-asset-log-use.component';

function use(overrides: Partial<DamAssetUtilization> = {}): DamAssetUtilization {
  return {
    id: 'u1',
    platformKey: 'newsletter',
    utilizedOn: '2026-10-04',
    utilizedDay: 'Sunday',
    campaignName: null,
    notes: null,
    ...overrides,
  };
}

@Component({
  standalone: true,
  imports: [DamAssetLogUseComponent],
  template: `
    <cp-dam-asset-log-use
      [open]="open()"
      [workspaceSlug]="slug()"
      [assetId]="assetId()"
      (logged)="logged.push($event)"
      (assetGone)="gone = gone + 1"
      (closed)="onClosed()"
    />
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly slug = signal('cozy-fall');
  readonly assetId = signal('a1');
  readonly logged: DamAssetUtilization[] = [];
  gone = 0;
  closes = 0;

  onClosed(): void {
    this.closes += 1;
    this.open.set(false);
  }
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamAssetLogUseComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let logSpy: jasmine.Spy<
    (slug: string, id: string, body: Record<string, unknown>, key: string) => Promise<DamUseLogOutcome>
  >;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;

  const today = damLocalDateValue(new Date());
  const tomorrow = damLocalDateValue(new Date(), 1);
  const dayAfter = damLocalDateValue(new Date(), 2);

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function field<T extends HTMLElement = HTMLInputElement>(id: string): T {
    const found = root().querySelector<T>(`[id="cp-dam-use-${id}"]`);
    if (!found) throw new Error(`no field "${id}"`);
    return found;
  }

  function type(id: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(id);
    control.value = value;
    control.dispatchEvent(new Event('input'));
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim().startsWith(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function describedBy(id: string): string {
    return (field(id).getAttribute('aria-describedby') ?? '')
      .split(' ')
      .filter(Boolean)
      .map((each) => root().querySelector(`[id="${each}"]`)?.textContent ?? '')
      .join(' ');
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  async function create(): Promise<void> {
    logSpy = jasmine.createSpy('logUtilization').and.resolveTo({ status: 'logged', use: use() });
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        { provide: DamAssetService, useValue: { logUtilization: logSpy } },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await settle();
  }

  // ---- Opening ----

  it('opens as a named modal dialog with an empty entry dated today', async () => {
    await create();

    const dialog = root().querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(root().querySelector(`#${dialog.getAttribute('aria-labelledby')}`)?.textContent).toContain('Log a use');

    expect(field('platform').value).toBe('');
    expect(field('date').value).toBe(today);
    expect(field('campaign').value).toBe('');
    expect(field<HTMLTextAreaElement>('notes').value).toBe('');
  });

  it('asks for a date and not a weekday, and lets the picker go no later than tomorrow', async () => {
    await create();

    expect(field('date').type).toBe('date');
    expect(field('date').getAttribute('max')).toBe(tomorrow);
    // No earliest date: a use from years ago is the creator entering their own history.
    expect(field('date').hasAttribute('min')).toBeFalse();
    expect(root().querySelector('select')).toBeNull();
    expect(text()).not.toMatch(/Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday/);
  });

  it('says once which fields are needed, marks them required, and labels every control', async () => {
    await create();

    expect(text()).toContain('Platform and date are needed. The rest is optional.');
    expect(text()).not.toContain('(optional)');
    expect(field('platform').getAttribute('aria-required')).toBe('true');
    expect(field('date').getAttribute('aria-required')).toBe('true');
    expect(field('campaign').getAttribute('aria-required')).toBeNull();

    for (const id of ['platform', 'date', 'campaign', 'notes']) {
      expect(root().querySelector(`label[for="cp-dam-use-${id}"]`)?.textContent?.trim()).withContext(id).not.toBe('');
    }
  });

  it("stops typing at each of the server's limits", async () => {
    await create();

    expect(field('platform').getAttribute('maxlength')).toBe('64');
    expect(field('campaign').getAttribute('maxlength')).toBe('200');
    expect(field('notes').getAttribute('maxlength')).toBe('2000');
  });

  // ---- Logging ----

  it('logs the use to this asset in this workspace, sending the date as a plain date and no weekday', async () => {
    await create();

    type('platform', ' newsletter ');
    type('date', '2026-10-04');
    type('campaign', 'Autumn bakes');
    type('notes', 'Lead picture.');
    await settle();
    button('Log this use').click();
    await settle();

    const [slug, id, body, key] = logSpy.calls.mostRecent().args;
    expect(slug).toBe('cozy-fall');
    expect(id).toBe('a1');
    expect(body).toEqual({
      platformKey: 'newsletter',
      utilizedOn: '2026-10-04',
      campaignName: 'Autumn bakes',
      notes: 'Lead picture.',
    });
    expect(Object.keys(body)).not.toContain('utilizedDay');
    expect(key.length).toBeGreaterThan(10);

    expect(host.logged).toEqual([use()]);
    expect(host.closes).toBe(1);
  });

  it('logs on Enter in a text box', async () => {
    await create();

    type('platform', 'newsletter');
    await settle();
    (root().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();

    expect(logSpy).toHaveBeenCalledTimes(1);
  });

  it('says it is logging, and cannot be logged twice or closed meanwhile', async () => {
    await create();
    let finish: (outcome: DamUseLogOutcome) => void = () => undefined;
    logSpy.and.returnValue(new Promise<DamUseLogOutcome>((resolve) => (finish = resolve)));

    type('platform', 'newsletter');
    await settle();
    button('Log this use').click();
    await settle();

    expect(button('Logging').disabled).toBeTrue();
    expect(button('Cancel').disabled).toBeTrue();
    button('Logging').click();
    button('Cancel').click();
    await settle();
    expect(logSpy).toHaveBeenCalledTimes(1);
    expect(host.closes).toBe(0);

    finish({ status: 'logged', use: use() });
    await settle();
    expect(host.closes).toBe(1);
  });

  // ---- Validation ----

  it('refuses a missing platform beside the field, focuses it, and sends nothing', async () => {
    await create();

    button('Log this use').click();
    await settle();
    await delay(10);
    fixture.detectChanges();

    expect(logSpy).not.toHaveBeenCalled();
    expect(describedBy('platform')).toContain('where it was used');
    expect(document.activeElement).toBe(field('platform'));
  });

  it('refuses a cleared date, and a date past tomorrow, beside the field', async () => {
    await create();
    type('platform', 'newsletter');

    type('date', '');
    await settle();
    button('Log this use').click();
    await settle();
    expect(describedBy('date')).toContain('Choose the date');

    type('date', dayAfter);
    await settle();
    button('Log this use').click();
    await settle();
    expect(describedBy('date')).toContain('in the future');
    expect(logSpy).not.toHaveBeenCalled();

    type('date', tomorrow);
    await settle();
    button('Log this use').click();
    await settle();
    expect(logSpy).toHaveBeenCalledTimes(1);
  });

  it("shows the server's own refusal beside the field it names, and keeps what was entered", async () => {
    await create();
    logSpy.and.resolveTo({
      status: 'invalid',
      message: 'That use could not be logged.',
      fieldErrors: { UtilizedOn: ['That date is too far ahead.'] },
    });

    type('platform', 'newsletter');
    await settle();
    button('Log this use').click();
    await settle();

    expect(describedBy('date')).toContain('That date is too far ahead.');
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('could not be logged as entered');
    expect(field('platform').value).toBe('newsletter');
    expect(host.closes).toBe(0);
  });

  // ---- Refusals ----

  it('logs nothing against a picture that has left the library, says so, and tells the page', async () => {
    await create();
    logSpy.and.resolveTo({ status: 'not_found' });

    type('platform', 'newsletter');
    await settle();
    button('Log this use').click();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain(
      "This picture is no longer in the library, so a use can't be logged.",
    );
    expect(host.logged).toEqual([]);
    expect(host.gone).toBe(1);
    // Trying again cannot succeed, so it is not offered.
    expect(button('Log this use').disabled).toBeTrue();

    // And there is nothing left worth asking about on the way out.
    button('Cancel').click();
    await settle();
    expect(confirmSpy).not.toHaveBeenCalled();
    expect(host.closes).toBe(1);
  });

  it('explains a refusal for lack of access and a failure, keeping the entry each time', async () => {
    await create();
    type('platform', 'newsletter');
    await settle();

    logSpy.and.resolveTo({ status: 'forbidden' });
    button('Log this use').click();
    await settle();
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('Contributor access');

    logSpy.and.resolveTo({ status: 'unavailable' });
    button('Log this use').click();
    await settle();
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('still here, so try again');

    expect(field('platform').value).toBe('newsletter');
    expect(host.closes).toBe(0);
    expect(host.gone).toBe(0);
  });

  // ---- One submission, one use ----

  it('retries the same entry under the same key, so a lost response cannot log one use as two', async () => {
    await create();
    logSpy.and.resolveTo({ status: 'unavailable' });

    type('platform', 'newsletter');
    await settle();
    button('Log this use').click();
    await settle();
    button('Log this use').click();
    await settle();

    const keys = logSpy.calls.allArgs().map((args) => args[3]);
    expect(keys.length).toBe(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('uses a new key once the entry changes, because it is then a different use', async () => {
    await create();
    logSpy.and.resolveTo({ status: 'unavailable' });

    type('platform', 'newsletter');
    await settle();
    button('Log this use').click();
    await settle();

    type('campaign', 'Autumn bakes');
    await settle();
    button('Log this use').click();
    await settle();

    const keys = logSpy.calls.allArgs().map((args) => args[3]);
    expect(keys[0]).not.toBe(keys[1]);
  });

  // ---- Cancel ----

  it('closes at once when nothing was entered', async () => {
    await create();

    button('Cancel').click();
    await settle();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(host.closes).toBe(1);
    expect(logSpy).not.toHaveBeenCalled();
  });

  it('asks before discarding an entry, and stays when the answer is no', async () => {
    await create();
    confirmSpy.and.resolveTo(false);

    type('notes', 'Half a thought');
    await settle();
    button('Cancel').click();
    await settle();

    expect(confirmSpy.calls.mostRecent().args[0].title).toBe('Discard this entry?');
    expect(host.closes).toBe(0);
    expect(field<HTMLTextAreaElement>('notes').value).toBe('Half a thought');

    confirmSpy.and.resolveTo(true);
    (root().querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement).click();
    await settle();
    expect(host.closes).toBe(1);
  });

  it('starts empty again each time it is opened, never carrying an entry from one asset or workspace to the next', async () => {
    await create();

    type('platform', 'typed-for-a');
    await settle();
    button('Cancel').click();
    await settle();

    host.slug.set('other-kitchen');
    host.assetId.set('b7');
    host.open.set(true);
    await settle();

    expect(field('platform').value).toBe('');
    expect(field('date').value).toBe(today);

    type('platform', 'newsletter');
    await settle();
    button('Log this use').click();
    await settle();

    expect(logSpy.calls.mostRecent().args.slice(0, 2)).toEqual(['other-kitchen', 'b7']);
    expect(JSON.stringify(logSpy.calls.mostRecent().args[2])).not.toContain('typed-for-a');
  });
});
