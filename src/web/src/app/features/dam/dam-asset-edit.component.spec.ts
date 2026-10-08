import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { DamAssetDetail } from '../../models/dam-asset.models';
import { WorkspaceTag } from '../../models/workspace-tag.models';
import { DamAssetDetailOutcome, DamAssetPatchOutcome, DamAssetService } from '../../services/dam-asset.service';
import { WorkspaceTagService, WorkspaceTagsOutcome } from '../../services/workspace-tag.service';
import { DamAssetEditComponent } from './dam-asset-edit.component';

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
  { key: 'vine', displayName: 'Vine', isActive: false },
];

const VOCABULARY: readonly WorkspaceTag[] = [
  { id: 't1', name: 'Weeknight' },
  { id: 't2', name: 'Baking' },
  { id: 't3', name: 'Autumn' },
];

function asset(overrides: Partial<DamAssetDetail> = {}): DamAssetDetail {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    description: 'Overhead.',
    altText: 'A round loaf on linen.',
    kind: 'Original',
    channelKey: 'instagram',
    platformKey: 'reels',
    day: 'Wednesday',
    styleKey: 'overhead-linen',
    rightsHolder: 'Sam Baker',
    attributionText: 'Photo: Sam Baker',
    tags: [{ id: 't1', name: 'Weeknight' }],
    currentVersion: null,
    versions: [],
    utilizationCount: 0,
    recipeLinks: [],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [],
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    concurrencyToken: 'token-1',
    ...overrides,
  };
}

@Component({
  standalone: true,
  imports: [DamAssetEditComponent],
  template: `
    <cp-dam-asset-edit
      [open]="open()"
      [workspaceSlug]="slug()"
      [asset]="asset()"
      [channels]="channels"
      (saved)="saved.push($event)"
      (refreshed)="refreshed.push($event)"
      (closed)="onClosed()"
    />
  `,
})
class HostComponent {
  readonly open = signal(false);
  readonly slug = signal('cozy-fall');
  readonly asset = signal(asset());
  readonly channels = CHANNELS;
  readonly saved: DamAssetDetail[] = [];
  readonly refreshed: DamAssetDetail[] = [];
  closes = 0;

  onClosed(): void {
    this.closes += 1;
    this.open.set(false);
  }
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamAssetEditComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let patchSpy: jasmine.Spy<
    (slug: string, id: string, body: Record<string, unknown>, key: string) => Promise<DamAssetPatchOutcome>
  >;
  let detailSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetDetailOutcome>>;
  let tagsSpy: jasmine.Spy<(slug: string) => Promise<WorkspaceTagsOutcome>>;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function field<T extends HTMLElement = HTMLInputElement>(id: string): T {
    const found = root().querySelector<T>(`[id="cp-dam-edit-${id}"]`);
    if (!found) throw new Error(`no field "${id}"`);
    return found;
  }

  function type(id: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(id);
    control.value = value;
    control.dispatchEvent(new Event('input'));
  }

  function choose(id: string, value: string): void {
    const control = field<HTMLSelectElement>(id);
    control.value = value;
    control.dispatchEvent(new Event('change'));
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim().startsWith(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function hasButton(label: string): boolean {
    return Array.from(root().querySelectorAll('button')).some((each) => each.textContent?.trim().startsWith(label));
  }

  function chosenTags(): string[] {
    return Array.from(root().querySelectorAll('.chosen .tag-name')).map((each) => each.textContent?.trim() ?? '');
  }

  /** Pick a tag in the combobox the way a keyboard user does: type, arrow down, Enter. */
  async function pickTag(name: string): Promise<void> {
    const input = field('add-tag');
    input.focus();
    input.value = name;
    input.dispatchEvent(new Event('input'));
    await settle();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    await settle();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  function lastBody(): Record<string, unknown> {
    return patchSpy.calls.mostRecent().args[2];
  }

  async function create(options: { tags?: WorkspaceTagsOutcome; asset?: DamAssetDetail } = {}): Promise<void> {
    patchSpy = jasmine.createSpy('patchMetadata').and.callFake(() =>
      Promise.resolve<DamAssetPatchOutcome>({ status: 'updated', asset: asset({ concurrencyToken: 'token-2' }) }),
    );
    detailSpy = jasmine.createSpy('detail');
    tagsSpy = jasmine
      .createSpy('list')
      .and.resolveTo(options.tags ?? ({ status: 'found', tags: VOCABULARY } as WorkspaceTagsOutcome));
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        { provide: DamAssetService, useValue: { patchMetadata: patchSpy, detail: detailSpy } },
        { provide: WorkspaceTagService, useValue: { list: tagsSpy } },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    if (options.asset) host.asset.set(options.asset);
    await settle();

    host.open.set(true);
    await settle();
  }

  // ---- Opening ----

  it('renders nothing and reads nothing while closed', async () => {
    await create();
    host.open.set(false);
    await settle();

    expect(root().querySelector('[role="dialog"]')).toBeNull();
  });

  it('opens as a named modal dialog holding the asset as it was read', async () => {
    await create();

    const dialog = root().querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(root().querySelector(`#${dialog.getAttribute('aria-labelledby')}`)?.textContent).toContain('Edit details');

    expect(field('title').value).toBe('Soda bread hero');
    expect(field<HTMLTextAreaElement>('description').value).toBe('Overhead.');
    expect(field<HTMLTextAreaElement>('alt-text').value).toBe('A round loaf on linen.');
    expect(field<HTMLSelectElement>('channel').value).toBe('instagram');
    expect(field<HTMLSelectElement>('day').value).toBe('Wednesday');
    expect(field('platform').value).toBe('reels');
    expect(field('style').value).toBe('overhead-linen');
    expect(field('rights-holder').value).toBe('Sam Baker');
    expect(field('attribution').value).toBe('Photo: Sam Baker');
    expect(chosenTags()).toEqual(['Weeknight']);
  });

  it('offers no control for the picture itself, its kind, or its cuisine and course', async () => {
    await create();

    expect(root().querySelector('input[type="file"]')).toBeNull();
    expect(text()).not.toMatch(/Cuisine|Course|AI generated|Uploaded/);
  });

  it('offers active channels, and keeps a retired one the asset already carries', async () => {
    await create({ asset: asset({ channelKey: 'vine' }) });

    const labels = Array.from(field<HTMLSelectElement>('channel').options).map((option) => option.textContent?.trim());
    expect(labels).toEqual(['No channel', 'Instagram', 'Pinterest', 'Vine (retired)']);
    expect(field<HTMLSelectElement>('channel').value).toBe('vine');
  });

  it('has nothing to save until something changes', async () => {
    await create();

    expect(button('Save changes').disabled).toBeTrue();

    type('title', 'Soda bread hero ');
    await settle();
    // Trailing whitespace is not a change: the server stores the title trimmed.
    expect(button('Save changes').disabled).toBeTrue();

    type('title', 'A better title');
    await settle();
    expect(button('Save changes').disabled).toBeFalse();
  });

  // ---- Saving ----

  it('saves only what changed, quoting the token it was opened with, and hands the answer back', async () => {
    await create();

    type('title', 'A better title');
    await settle();
    button('Save changes').click();
    await settle();

    expect(patchSpy).toHaveBeenCalledTimes(1);
    const [slug, id, body, key] = patchSpy.calls.mostRecent().args;
    expect(slug).toBe('cozy-fall');
    expect(id).toBe('a1');
    expect(body).toEqual({ expectedConcurrencyToken: 'token-1', title: 'A better title' });
    expect(key.length).toBeGreaterThan(10);

    expect(host.saved.length).toBe(1);
    expect(host.saved[0].concurrencyToken).toBe('token-2');
    expect(host.closes).toBe(1);
  });

  it('sends an emptied field as a clear, and never mentions a field that was not touched', async () => {
    await create();

    type('alt-text', '');
    choose('channel', '');
    choose('day', '');
    await settle();
    button('Save changes').click();
    await settle();

    expect(lastBody()).toEqual({ expectedConcurrencyToken: 'token-1', altText: null, channelKey: null, day: null });
  });

  it('saves on Enter in a text box', async () => {
    await create();

    type('title', 'A better title');
    await settle();
    (root().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();

    expect(patchSpy).toHaveBeenCalledTimes(1);
  });

  it('says it is saving, and cannot be saved twice or closed meanwhile', async () => {
    await create();
    let finish: (outcome: DamAssetPatchOutcome) => void = () => undefined;
    patchSpy.and.returnValue(new Promise<DamAssetPatchOutcome>((resolve) => (finish = resolve)));

    type('title', 'A better title');
    await settle();
    button('Save changes').click();
    await settle();

    expect(button('Saving').disabled).toBeTrue();
    expect(button('Cancel').disabled).toBeTrue();
    button('Cancel').click();
    await settle();
    expect(host.closes).toBe(0);

    finish({ status: 'updated', asset: asset() });
    await settle();
    expect(host.closes).toBe(1);
  });

  // ---- Validation ----

  it('refuses an empty title beside the field, focuses it, and sends nothing', async () => {
    await create();

    type('title', '   ');
    await settle();
    button('Save changes').click();
    await settle();
    await delay(10);
    fixture.detectChanges();

    expect(patchSpy).not.toHaveBeenCalled();
    const control = field('title');
    const describedBy = (control.getAttribute('aria-describedby') ?? '').split(' ').filter(Boolean);
    const messages = describedBy.map((id) => root().querySelector(`[id="${id}"]`)?.textContent ?? '').join(' ');
    expect(messages).toContain('needs a title');
    expect(document.activeElement).toBe(control);
  });

  it("stops typing at each of the server's limits", async () => {
    await create();

    expect(field('title').getAttribute('maxlength')).toBe('200');
    expect(field('description').getAttribute('maxlength')).toBe('2000');
    expect(field('alt-text').getAttribute('maxlength')).toBe('1000');
    expect(field('platform').getAttribute('maxlength')).toBe('64');
    expect(field('style').getAttribute('maxlength')).toBe('64');
    expect(field('rights-holder').getAttribute('maxlength')).toBe('500');
    expect(field('attribution').getAttribute('maxlength')).toBe('500');
  });

  it("shows the server's own refusal beside the field it names, and keeps what was typed", async () => {
    await create();
    patchSpy.and.resolveTo({
      status: 'invalid',
      message: 'That edit could not be saved.',
      fieldErrors: { PlatformKey: ['That platform key is not allowed.'] },
    });

    type('platform', 'bad key');
    await settle();
    button('Save changes').click();
    await settle();

    expect(text()).toContain('That platform key is not allowed.');
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('could not be saved');
    expect(field('platform').value).toBe('bad key');
    expect(host.closes).toBe(0);

    // Correcting the field clears its error.
    type('platform', 'stories');
    await settle();
    expect(text()).not.toContain('That platform key is not allowed.');
  });

  // ---- Concurrency conflict ----

  it('keeps everything typed on a conflict, says nothing was saved, and blocks saving until the latest is loaded', async () => {
    await create();
    patchSpy.and.resolveTo({ status: 'stale' });

    type('title', 'My title');
    type('description', 'My description');
    await settle();
    button('Save changes').click();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('Someone else changed this picture');
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('nothing was saved');
    expect(field('title').value).toBe('My title');
    expect(field<HTMLTextAreaElement>('description').value).toBe('My description');
    expect(host.closes).toBe(0);
    expect(host.saved).toEqual([]);
    expect(button('Save changes').disabled).toBeTrue();
    expect(hasButton('Load the latest and keep my changes')).toBeTrue();
  });

  it("loads the latest onto the form: the creator's changes stay, untouched fields take the newer values, and the new token is quoted", async () => {
    await create();
    patchSpy.and.resolveTo({ status: 'stale' });
    const latest = asset({
      title: 'Their title',
      attributionText: 'Their credit',
      tags: [
        { id: 't1', name: 'Weeknight' },
        { id: 't2', name: 'Baking' },
      ],
      concurrencyToken: 'token-9',
    });
    detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'found', asset: latest }));

    type('title', 'My title');
    await settle();
    button('Save changes').click();
    await settle();
    button('Load the latest and keep my changes').click();
    await settle();

    expect(detailSpy).toHaveBeenCalledOnceWith('cozy-fall', 'a1');
    expect(field('title').value).toBe('My title');
    expect(field('attribution').value).toBe('Their credit');
    expect(chosenTags()).toEqual(['Weeknight', 'Baking']);
    expect(root().querySelector('[role="alert"]')).toBeNull();
    expect(text()).toContain('your changes are still here');
    // The page behind the dialog is told too, so it is not left showing the stale asset.
    expect(host.refreshed).toEqual([latest]);

    patchSpy.and.resolveTo({ status: 'updated', asset: asset({ title: 'My title', concurrencyToken: 'token-10' }) });
    button('Save changes').click();
    await settle();

    // Only the creator's own change, against the new token — never a stale copy of what someone else edited.
    expect(lastBody()).toEqual({ expectedConcurrencyToken: 'token-9', title: 'My title' });
    expect(host.closes).toBe(1);
  });

  it('uses a fresh idempotency key after a conflict, because it is a different edit', async () => {
    await create();
    patchSpy.and.resolveTo({ status: 'stale' });
    detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'found', asset: asset({ concurrencyToken: 'token-9' }) }));

    type('title', 'My title');
    await settle();
    button('Save changes').click();
    await settle();
    button('Load the latest and keep my changes').click();
    await settle();
    button('Save changes').click();
    await settle();

    const keys = patchSpy.calls.allArgs().map((args) => args[3]);
    expect(keys.length).toBe(2);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('reuses the idempotency key when the same edit is retried after a failure', async () => {
    await create();
    patchSpy.and.resolveTo({ status: 'unavailable' });

    type('title', 'My title');
    await settle();
    button('Save changes').click();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('still here');
    button('Save changes').click();
    await settle();

    const keys = patchSpy.calls.allArgs().map((args) => args[3]);
    expect(keys[0]).toBe(keys[1]);
  });

  it('says so when the latest already has everything the creator changed, leaving nothing to save', async () => {
    await create();
    patchSpy.and.resolveTo({ status: 'stale' });
    detailSpy.and.returnValue(
      of<DamAssetDetailOutcome>({ status: 'found', asset: asset({ title: 'Same title', concurrencyToken: 'token-9' }) }),
    );

    type('title', 'Same title');
    await settle();
    button('Save changes').click();
    await settle();
    button('Load the latest and keep my changes').click();
    await settle();

    expect(text()).toContain('nothing left to save');
    expect(button('Save changes').disabled).toBeTrue();
  });

  it('keeps the changes when the latest cannot be loaded', async () => {
    await create();
    patchSpy.and.resolveTo({ status: 'stale' });
    detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'unavailable' }));

    type('title', 'My title');
    await settle();
    button('Save changes').click();
    await settle();
    button('Load the latest and keep my changes').click();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('could not be loaded');
    expect(field('title').value).toBe('My title');
    expect(hasButton('Load the latest and keep my changes')).toBeTrue();
  });

  // ---- Other refusals ----

  it('explains a refusal for lack of access, a vanished asset and a failure, keeping the form each time', async () => {
    await create();
    type('title', 'My title');
    await settle();

    for (const [outcome, sentence] of [
      [{ status: 'forbidden' }, 'Contributor access'],
      [{ status: 'not_found' }, 'no longer in the library'],
      [{ status: 'unavailable' }, 'could not be saved just now'],
    ] as const) {
      patchSpy.and.resolveTo(outcome as DamAssetPatchOutcome);
      button('Save changes').click();
      await settle();

      expect(root().querySelector('[role="alert"]')?.textContent).toContain(sentence);
      expect(field('title').value).toBe('My title');
    }

    expect(host.closes).toBe(0);
  });

  // ---- Tags ----

  it('adds a tag from the workspace list and removes one, sending the whole new set', async () => {
    await create();

    await pickTag('Baking');
    button('Add tag').click();
    await settle();
    expect(chosenTags()).toEqual(['Weeknight', 'Baking']);

    (root().querySelector('button[aria-label="Remove the tag Weeknight"]') as HTMLButtonElement).click();
    await settle();
    expect(chosenTags()).toEqual(['Baking']);

    button('Save changes').click();
    await settle();

    expect(lastBody()).toEqual({ expectedConcurrencyToken: 'token-1', tags: ['t2'] });
  });

  it('offers only tags the picture does not already carry, and cannot add one until it is picked', async () => {
    await create();

    expect(button('Add tag').disabled).toBeTrue();
    expect(tagsSpy).toHaveBeenCalledOnceWith('cozy-fall');

    await pickTag('Weeknight');
    // Already on the picture, so it is not among the choices and nothing was picked.
    expect(button('Add tag').disabled).toBeTrue();
  });

  it('sends an empty list when every tag is removed', async () => {
    await create();

    (root().querySelector('button[aria-label="Remove the tag Weeknight"]') as HTMLButtonElement).click();
    await settle();

    expect(text()).toContain('No tags on this picture.');
    button('Save changes').click();
    await settle();

    expect(lastBody()).toEqual({ expectedConcurrencyToken: 'token-1', tags: [] });
  });

  it('still lets a tag be removed when the workspace list cannot be loaded, and offers a retry', async () => {
    await create({ tags: { status: 'unavailable' } });

    expect(text()).toContain("couldn't be loaded");
    expect(root().querySelector('[id="cp-dam-edit-add-tag"]')).toBeNull();
    // The asset's own tag is named from the asset, not from the list that failed.
    expect(chosenTags()).toEqual(['Weeknight']);

    tagsSpy.and.resolveTo({ status: 'found', tags: VOCABULARY });
    button('Try again').click();
    await settle();

    expect(root().querySelector('[id="cp-dam-edit-add-tag"]')).toBeTruthy();
  });

  it('says where tags come from when the workspace has none', async () => {
    await create({ tags: { status: 'found', tags: [] }, asset: asset({ tags: [] }) });

    expect(text()).toContain('no tags yet');
    expect(text()).toContain('when you tag a recipe');
    expect(root().querySelector('[id="cp-dam-edit-add-tag"]')).toBeNull();
  });

  it('names a retired tag the picture already carries, though the list no longer offers it', async () => {
    await create({ asset: asset({ tags: [{ id: 'old', name: 'Retired tag' }] }) });

    expect(chosenTags()).toEqual(['Retired tag']);
  });

  // ---- Cancel ----

  it('closes at once when nothing was changed', async () => {
    await create();

    button('Cancel').click();
    await settle();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(host.closes).toBe(1);
  });

  it('asks before discarding changes, and stays when the answer is no', async () => {
    await create();
    confirmSpy.and.resolveTo(false);

    type('title', 'My title');
    await settle();
    button('Cancel').click();
    await settle();

    expect(confirmSpy).toHaveBeenCalledTimes(1);
    expect(host.closes).toBe(0);
    expect(field('title').value).toBe('My title');

    confirmSpy.and.resolveTo(true);
    button('Cancel').click();
    await settle();

    expect(host.closes).toBe(1);
    expect(patchSpy).not.toHaveBeenCalled();
  });

  it('treats Escape and the close button as Cancel, but leaves Escape in the tag picker to the picker', async () => {
    await create();
    confirmSpy.and.resolveTo(false);
    type('title', 'My title');
    await settle();

    field('add-tag').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();
    expect(confirmSpy).not.toHaveBeenCalled();

    field('title').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();
    expect(confirmSpy).toHaveBeenCalledTimes(1);

    (root().querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement).click();
    await settle();
    expect(confirmSpy).toHaveBeenCalledTimes(2);
    expect(host.closes).toBe(0);
  });

  it('starts from the asset again each time it is opened, not from a previous abandoned edit', async () => {
    await create();

    type('title', 'Abandoned');
    await settle();
    button('Cancel').click();
    await settle();

    host.open.set(true);
    await settle();

    expect(field('title').value).toBe('Soda bread hero');
    expect(button('Save changes').disabled).toBeTrue();
  });

  // ---- Two workspaces ----

  it("reads tags from, and saves to, the workspace it was opened in — never another's", async () => {
    await create();
    host.open.set(false);
    host.slug.set('other-kitchen');
    await settle();
    host.open.set(true);
    await settle();

    expect(tagsSpy.calls.mostRecent().args).toEqual(['other-kitchen']);

    type('title', 'Other title');
    await settle();
    button('Save changes').click();
    await settle();

    expect(patchSpy.calls.mostRecent().args[0]).toBe('other-kitchen');
  });

  // ---- Accessibility ----

  it('labels every control, marks the title required, and names each section', async () => {
    await create();

    for (const id of ['title', 'description', 'alt-text', 'channel', 'day', 'platform', 'style', 'rights-holder', 'attribution', 'add-tag']) {
      const label = root().querySelector(`label[for="cp-dam-edit-${id}"]`);
      expect(label?.textContent?.trim()).withContext(id).not.toBe('');
    }

    expect(field('title').getAttribute('aria-required')).toBe('true');
    expect(Array.from(root().querySelectorAll('cp-form-section h3, cp-form-section h2')).map((h) => h.textContent?.trim())).toEqual([
      'What it is',
      'What it is for',
      'Rights',
      'Tags',
    ]);
    // Optionality is said once, not field by field.
    expect(text()).toContain('Only the title is needed');
    expect(text()).not.toContain('(optional)');
  });

  it('has a status region waiting for the conflict outcome before there is one', async () => {
    await create();

    const quiet = Array.from(root().querySelectorAll('cp-notice[role="status"]')).find(
      (each) => (each.textContent ?? '').trim() === '',
    );
    expect(quiet).toBeTruthy();
  });
});
