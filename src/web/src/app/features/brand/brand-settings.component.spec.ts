import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { Observable, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { VisibilityService } from '../../core/visibility.service';
import { DamAssetDetailOutcome, DamAssetSearchOutcome, DamAssetService } from '../../services/dam-asset.service';
import { assetDetail, assetSummary } from '../recipes/recipe-media.testing';
import { WorkspaceRole } from '../../models/auth.models';
import { BrandProfile, ContentChannel, decodeBrandProfile } from '../../models/brand-profile.models';
import { BrandProfileOutcome, BrandProfileService, ContentChannelsOutcome, SaveBrandProfileOutcome } from '../../services/brand-profile.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { BrandSettingsComponent } from './brand-settings.component';
import { brandSettingsCanDeactivateGuard } from './brand-settings.guard';

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'tiktok', displayName: 'TikTok', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
];

function profile(overrides: Record<string, unknown> = {}): BrandProfile {
  return decodeBrandProfile({
    id: 'p1',
    brandName: "Sam's Kitchen",
    shortDescription: 'Weeknight cooking.',
    defaultAudience: null,
    locale: 'en-US',
    timeZoneId: 'America/Chicago',
    channelDefaults: [{ channelKey: 'instagram' }],
    links: [{ kind: 'Website', url: 'https://example.com', label: 'Home' }],
    assets: [],
    revision: 1,
    createdAt: '2026-09-30T12:00:00Z',
    updatedAt: '2026-09-30T12:00:00Z',
    concurrencyToken: 'tok-1',
    ...overrides,
  })!;
}

function membershipStub(role: WorkspaceRole | null) {
  const state = signal<MyMembershipsState>(
    role === null
      ? { status: 'loading' }
      : {
          status: 'ready',
          memberships: [
            { workspaceId: 'w1', workspaceSlug: 'sams-kitchen', workspaceName: 'Sams', membershipId: 'm1', role, status: 'Active' },
          ],
        },
  );
  return { state, ensureLoaded: () => Promise.resolve() };
}

interface Harness {
  readonly get: jasmine.Spy<(slug: string) => Promise<BrandProfileOutcome>>;
  readonly create: jasmine.Spy<(slug: string, body: Record<string, unknown>, key?: string) => Promise<SaveBrandProfileOutcome>>;
  readonly update: jasmine.Spy<(slug: string, body: Record<string, unknown>, key?: string) => Promise<SaveBrandProfileOutcome>>;
  readonly channels: jasmine.Spy<() => Promise<ContentChannelsOutcome>>;
  readonly confirm: jasmine.Spy;
  readonly assetDetail: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetDetailOutcome>>;
}

let harness: RouterTestingHarness;
let spies: Harness;

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function create(options: {
  read?: BrandProfileOutcome;
  channels?: ContentChannelsOutcome;
  role?: WorkspaceRole | null;
  hold?: boolean;
} = {}): Promise<void> {
  const read = options.read ?? { status: 'found', profile: profile() };
  spies = {
    get: jasmine.createSpy('get').and.callFake(() =>
      options.hold ? new Promise<BrandProfileOutcome>(() => undefined) : Promise.resolve(read),
    ),
    create: jasmine.createSpy('create').and.resolveTo({ status: 'saved', profile: profile(), replayed: false }),
    update: jasmine.createSpy('update').and.resolveTo({ status: 'saved', profile: profile({ revision: 2, concurrencyToken: 'tok-2' }), replayed: false }),
    channels: jasmine.createSpy('channels').and.resolveTo(options.channels ?? { status: 'found', channels: CHANNELS }),
    confirm: jasmine.createSpy('confirm').and.resolveTo(true),
    assetDetail: jasmine
      .createSpy('assetDetail')
      .and.callFake((_slug, id) => of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({ id, title: `Logo ${id}` }) })),
  };

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/brand', component: BrandSettingsComponent, canDeactivate: [brandSettingsCanDeactivateGuard] },
        { path: ':workspaceSlug/elsewhere', component: BrandSettingsComponent },
      ]),
      {
        provide: BrandProfileService,
        useValue: {
          getBrandProfile: spies.get,
          createBrandProfile: spies.create,
          updateBrandProfile: spies.update,
          listContentChannels: spies.channels,
        },
      },
      { provide: WorkspaceMembershipService, useValue: membershipStub(options.role === undefined ? 'Editor' : options.role) },
      { provide: ConfirmService, useValue: { confirm: spies.confirm } },
      {
        provide: DamAssetService,
        useValue: {
          detail: spies.assetDetail,
          content: () => of({ status: 'gone' }),
          versionContent: () => of({ status: 'gone' }),
          search: () =>
            of<DamAssetSearchOutcome>({
              status: 'found',
              page: {
                items: [assetSummary({ id: 'a1', title: 'Wordmark' }), assetSummary({ id: 'a2', title: 'Mark only' })],
                nextCursor: null,
                totalCount: 2,
              },
            }),
        },
      },
      { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
    ],
  }).compileComponents();

  harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/sams-kitchen/brand', BrandSettingsComponent);
  await settle();
}

async function settle(): Promise<void> {
  await delay(0);
  harness.detectChanges();
  await delay(0);
  harness.detectChanges();
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function text(): string {
  return root().textContent ?? '';
}

function input(id: string): HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement {
  const element = root().querySelector(`[id="${id}"]`);
  if (!element) throw new Error(`no control #${id}`);
  return element as HTMLInputElement;
}

async function type(id: string, value: string): Promise<void> {
  const control = input(id);
  control.value = value;
  control.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
}

function button(label: string): HTMLButtonElement {
  const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label));
  if (!match) throw new Error(`no button labelled "${label}"`);
  return match;
}

async function save(): Promise<void> {
  button('Save changes').click();
  await settle();
}

function component(): BrandSettingsComponent {
  return harness.routeDebugElement!.componentInstance as BrandSettingsComponent;
}

describe('BrandSettingsComponent', () => {
  // ---- States ----

  it('shows a loading status while the profile is in flight', async () => {
    await create({ hold: true });

    expect(root().querySelector('[role="status"]')?.textContent).toContain('Loading');
    expect(root().querySelector('form')).toBeNull();
  });

  it('links to the Create my voice wizard for the same workspace', async () => {
    await create();
    const link = Array.from(root().querySelectorAll('a')).find((a) => a.textContent?.trim() === 'Create my voice');
    expect(link?.getAttribute('href')).toBe('/sams-kitchen/brand/setup');
  });

  it('reads the profile for the workspace named by the route', async () => {
    await create();

    expect(spies.get).toHaveBeenCalledWith('sams-kitchen');
  });

  it('fills the form from the profile', async () => {
    await create();

    expect(input('brand-name').value).toBe("Sam's Kitchen");
    expect(input('brand-short-description').value).toBe('Weeknight cooking.');
    expect(input('brand-locale').value).toBe('en-US');
    expect(input('brand-time-zone').value).toBe('America/Chicago');
    expect((input('brand-channel-instagram') as HTMLInputElement).checked).toBeTrue();
    expect((input('brand-channel-tiktok') as HTMLInputElement).checked).toBeFalse();
    expect(root().querySelectorAll('li.row').length).toBe(1);
  });

  it('shows the first-use state with an empty form and a create button', async () => {
    await create({ read: { status: 'first_use' } });

    expect(text()).toContain('Set up your brand');
    expect(input('brand-name').value).toBe('');
    expect(text()).toContain('Create brand settings');
    expect(root().querySelector('#brand-reason')).toBeNull();
  });

  it('shows an error with a retry when the profile cannot be loaded', async () => {
    await create({ read: { status: 'unavailable' } });

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");

    spies.get.and.resolveTo({ status: 'found', profile: profile() });
    button('Try again').click();
    await settle();

    expect(input('brand-name').value).toBe("Sam's Kitchen");
  });

  it('shows not found for an unknown workspace', async () => {
    await create({ read: { status: 'not_found' } });

    expect(text()).toContain("couldn't find this workspace");
  });

  // ---- Saving ----

  it('creates the profile from the first-use form, sending only what was filled in', async () => {
    await create({ read: { status: 'first_use' } });

    await type('brand-name', '  Sam  ');
    button('Create brand settings').click();
    await settle();

    expect(spies.create).toHaveBeenCalledTimes(1);
    const [slug, body, key] = spies.create.calls.mostRecent().args;
    expect(slug).toBe('sams-kitchen');
    expect(body).toEqual({ brandName: 'Sam' });
    expect(key).toMatch(/[0-9a-f-]{36}/);
    expect(text()).toContain('Brand settings saved.');
    expect(text()).toContain('Save changes');
  });

  it('sends a merge patch holding only the changed field and the token', async () => {
    await create();

    await type('brand-default-audience', 'Busy home cooks');
    await save();

    expect(spies.update.calls.mostRecent().args[1]).toEqual({
      expectedConcurrencyToken: 'tok-1',
      defaultAudience: 'Busy home cooks',
    });
  });

  it('sends null for a cleared optional field and includes the reason', async () => {
    await create();

    await type('brand-short-description', '   ');
    await type('brand-reason', 'Simplifying');
    await save();

    expect(spies.update.calls.mostRecent().args[1]).toEqual({
      expectedConcurrencyToken: 'tok-1',
      reason: 'Simplifying',
      shortDescription: null,
    });
  });

  it('sends the whole channel list when a channel is ticked', async () => {
    await create();

    const tiktok = input('brand-channel-tiktok') as HTMLInputElement;
    tiktok.click();
    await settle();
    await save();

    expect(spies.update.calls.mostRecent().args[1]['channelDefaults']).toEqual([
      { channelKey: 'instagram' },
      { channelKey: 'tiktok' },
    ]);
  });

  it('says so and makes no request when nothing changed', async () => {
    await create();

    await save();

    expect(spies.update).not.toHaveBeenCalled();
    expect(text()).toContain('nothing new to save');
  });

  it('rebinds to the saved profile so the next save quotes the new token', async () => {
    await create();

    await type('brand-default-audience', 'A');
    await save();
    await type('brand-default-audience', 'B');
    await save();

    expect(spies.update.calls.mostRecent().args[1]['expectedConcurrencyToken']).toBe('tok-2');
  });

  it('refuses a blank name locally, with the error beside the field and focus on it', async () => {
    await create({ read: { status: 'first_use' } });

    button('Create brand settings').click();
    await settle();

    expect(spies.create).not.toHaveBeenCalled();
    expect(text()).toContain('A brand needs a name.');
    expect(document.activeElement?.id).toBe('brand-name');
    expect(input('brand-name').getAttribute('aria-invalid')).toBe('true');
  });

  it('shows server field errors beside the fields and focuses the first', async () => {
    await create();
    spies.update.and.resolveTo({
      status: 'validation_failed',
      fieldErrors: { timeZoneId: ['That is not a time zone we recognise.'], locale: ['Use a language tag such as en-US.'] },
    });

    await type('brand-default-audience', 'X');
    await save();

    expect(text()).toContain('Use a language tag such as en-US.');
    expect(text()).toContain('That is not a time zone we recognise.');
    // Locale comes before the time zone on the page, so it gets focus.
    expect(document.activeElement?.id).toBe('brand-locale');
    // The creator's edit is still in the form.
    expect(input('brand-default-audience').value).toBe('X');
  });

  it('puts a link error on the row that caused it', async () => {
    await create();
    spies.update.and.resolveTo({
      status: 'validation_failed',
      fieldErrors: { 'links[0].url': ['Use an http or https address without a username or password.'] },
    });

    await type('brand-default-audience', 'X');
    await save();

    const row = root().querySelector('li.row')!;
    expect(row.textContent).toContain('Use an http or https address');
  });

  it('keeps the creator\'s edits on a conflict and refreshes the fields they did not touch', async () => {
    await create();
    spies.update.and.resolveTo({ status: 'conflict' });
    spies.get.and.resolveTo({
      status: 'found',
      profile: profile({ shortDescription: 'Changed by someone else.', revision: 2, concurrencyToken: 'tok-9' }),
    });

    await type('brand-default-audience', 'My audience');
    await save();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('Someone else changed');
    expect(input('brand-default-audience').value).toBe('My audience');
    expect(input('brand-short-description').value).toBe('Changed by someone else.');

    // Saving again quotes the latest token.
    spies.update.and.resolveTo({ status: 'saved', profile: profile({ revision: 3, concurrencyToken: 'tok-10' }), replayed: false });
    await save();
    expect(spies.update.calls.mostRecent().args[1]['expectedConcurrencyToken']).toBe('tok-9');
  });

  it('keeps the edits and offers another try when saving is unavailable', async () => {
    await create();
    spies.update.and.resolveTo({ status: 'unavailable' });

    await type('brand-default-audience', 'Keep me');
    await save();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be saved");
    expect(input('brand-default-audience').value).toBe('Keep me');
  });

  it('reuses the idempotency key for a retry of the same body and changes it when the body changes', async () => {
    await create();
    spies.update.and.resolveTo({ status: 'unavailable' });

    await type('brand-default-audience', 'One');
    await save();
    await save();
    const [first, second] = spies.update.calls.allArgs().map((args) => args[2]);
    expect(second).toBe(first);

    await type('brand-default-audience', 'Two');
    await save();
    expect(spies.update.calls.mostRecent().args[2]).not.toBe(first);
  });

  // ---- Links ----

  it('adds a link row and focuses its address, then removes it', async () => {
    await create();

    button('Add a link').click();
    await settle();
    expect(root().querySelectorAll('li.row').length).toBe(2);
    expect(document.activeElement?.id).toMatch(/^brand-link-link-\d+-url$/);

    const remove = root().querySelectorAll<HTMLButtonElement>('li.row button')[1];
    remove.click();
    await settle();
    expect(root().querySelectorAll('li.row').length).toBe(1);
  });

  it('stops offering new links at the cap', async () => {
    await create({ read: { status: 'first_use' } });

    for (let i = 0; i < 20; i++) component().addLink();
    await settle();

    expect(button('Add a link').disabled).toBeTrue();
    expect(text()).toContain('up to 20 links');
  });

  // ---- Channels ----

  it('lists a retired stored channel, marked, so it can be unticked', async () => {
    await create({
      read: { status: 'found', profile: profile({ channelDefaults: [{ channelKey: 'myspace' }] }) },
    });

    expect(text()).toContain('myspace (unknown channel)');
  });

  it('degrades when the channel list cannot load: explains, keeps stored keys, and never sends them', async () => {
    await create({ channels: { status: 'unavailable' } });

    expect(text()).toContain("list of channels couldn't be loaded");
    expect(text()).toContain('Currently chosen: instagram');

    await type('brand-default-audience', 'X');
    await save();

    expect(spies.update.calls.mostRecent().args[1]['channelDefaults']).toBeUndefined();
  });

  // ---- Roles ----

  it('is read-only for a viewer or contributor: fields disabled, no save, and a note saying why', async () => {
    for (const role of ['Viewer', 'Contributor'] as const) {
      await create({ role });

      expect(root().querySelector('fieldset')?.disabled).toBeTrue();
      expect(root().querySelector('button[type="submit"]')).toBeNull();
      expect(text()).toContain('An Editor or Owner of this workspace can change them');
    }
  });

  it('allows an owner to edit', async () => {
    await create({ role: 'Owner' });

    expect(root().querySelector('fieldset')?.disabled).toBeFalse();
    expect(root().querySelector('button[type="submit"]')).toBeTruthy();
  });

  // ---- Scope: no voice, style or AI ----

  it('points to the Brand Style Guide rather than editing voice or style, and makes no AI or training claim', async () => {
    await create();

    const style = root().querySelector('#section-style')!;
    expect(style.textContent).toContain('Brand Style Guide');
    expect(style.querySelector('input,textarea,select,a')).toBeNull();
    expect(text()).not.toMatch(/\b(AI|train|trained|training|generate|generated)\b/i);
  });

  // ---- Logos (12.10k) ----

  describe('logos', () => {
    const LOGOS = [
      { mediaAssetId: 'a1', role: 'PrimaryLogo' },
      { mediaAssetId: 'a2', role: 'AlternateLogo' },
    ];

    function logoSection(): HTMLElement {
      return root().querySelector<HTMLElement>('#section-logo')!;
    }

    async function chooseFromLibrary(opener: string, title: string): Promise<void> {
      button(opener).click();
      await settle();
      root().querySelector<HTMLButtonElement>(`button[aria-label="Choose ${title}"]`)!.click();
      await settle();
    }

    it('says when there is no logo and offers to choose one from the library', async () => {
      await create();

      expect(logoSection().textContent).toContain('No primary logo chosen.');
      expect(button('Choose from library').disabled).toBeFalse();
      expect(logoSection().textContent).not.toContain("isn't available yet");
    });

    it('shows the linked logos by name, each linking to its picture in the library', async () => {
      await create({ read: { status: 'found', profile: profile({ assets: LOGOS }) } });

      const links = Array.from(logoSection().querySelectorAll('a')).map((each) => [each.textContent?.trim(), each.getAttribute('href')]);

      expect(links).toEqual([
        ['Logo a1', '/sams-kitchen/dam/a1'],
        ['Logo a2', '/sams-kitchen/dam/a2'],
      ]);
    });

    it('holds a chosen logo in the form until Save, then sends the whole list', async () => {
      await create();

      await chooseFromLibrary('Choose from library', 'Wordmark');

      // Nothing has been sent: the logo is an edit like any other.
      expect(spies.update).not.toHaveBeenCalled();
      expect(root().querySelector('[role="dialog"]')).toBeNull();
      expect(logoSection().textContent).toContain('Logo a1');

      await chooseFromLibrary('Add from library', 'Mark only');
      await save();

      expect(spies.update.calls.mostRecent().args[1]).toEqual({
        expectedConcurrencyToken: 'tok-1',
        assets: [
          { mediaAssetId: 'a1', role: 'PrimaryLogo' },
          { mediaAssetId: 'a2', role: 'AlternateLogo' },
        ],
      });
    });

    it('counts a chosen logo as an unsaved change, and asks before leaving with one', async () => {
      await create();
      await chooseFromLibrary('Choose from library', 'Wordmark');

      spies.confirm.and.resolveTo(false);
      await harness.navigateByUrl('/sams-kitchen/elsewhere').catch(() => undefined);
      await settle();

      expect(spies.confirm).toHaveBeenCalled();
    });

    it('replaces the primary logo rather than keeping the old one as an alternate', async () => {
      await create({ read: { status: 'found', profile: profile({ assets: [{ mediaAssetId: 'a9', role: 'PrimaryLogo' }] }) } });

      await chooseFromLibrary('Change', 'Wordmark');
      await save();

      expect(spies.update.calls.mostRecent().args[1]['assets']).toEqual([{ mediaAssetId: 'a1', role: 'PrimaryLogo' }]);
    });

    it('unlinks a logo by sending the list without it, and an empty list for the last one', async () => {
      await create({ read: { status: 'found', profile: profile({ assets: LOGOS }) } });

      root().querySelector<HTMLButtonElement>('button[aria-label="Unlink other logo 1"]')!.click();
      await settle();
      root().querySelector<HTMLButtonElement>('button[aria-label="Unlink the primary logo"]')!.click();
      await settle();

      expect(spies.update).not.toHaveBeenCalled();
      await save();

      expect(spies.update.calls.mostRecent().args[1]['assets']).toEqual([]);
    });

    it('never says a picture is deleted or removed, and offers nothing that would change the library', async () => {
      await create({ read: { status: 'found', profile: profile({ assets: LOGOS }) } });

      const words = logoSection().textContent?.toLowerCase() ?? '';
      const labels = Array.from(logoSection().querySelectorAll('button')).map((each) =>
        `${each.textContent ?? ''} ${each.getAttribute('aria-label') ?? ''}`.toLowerCase(),
      );

      expect(words).not.toMatch(/delete|remove/);
      expect(labels.some((label) => /upload|edit|delete|remove/.test(label))).toBeFalse();
      expect(logoSection().querySelector('input[type="file"]')).toBeNull();
    });

    it('marks the logo the server refused on its own row, and clears the mark when the logos change', async () => {
      await create({ read: { status: 'found', profile: profile({ assets: [{ mediaAssetId: 'a1', role: 'PrimaryLogo' }] }) } });
      spies.update.and.resolveTo({
        status: 'assets_refused',
        positions: [1],
        message: "That picture is not in this workspace's library.",
      });

      await chooseFromLibrary('Add from library', 'Mark only');
      await save();

      const refused = logoSection().querySelector('[data-logo-id="a2"] [role="alert"]');
      expect(refused?.textContent).toContain("That picture is not in this workspace's library. Unlink it or choose another.");
      expect(logoSection().querySelector('[data-logo-id="a1"] [role="alert"]')).toBeNull();

      // The logo itself is still in the form, for the creator to act on.
      expect(logoSection().textContent).toContain('Logo a2');

      root().querySelector<HTMLButtonElement>('button[aria-label="Unlink other logo 1"]')!.click();
      await settle();

      expect(logoSection().querySelector('[role="alert"]')).toBeNull();
    });

    it('does not save the settings when Enter is pressed in the library search', async () => {
      await create();
      await type('brand-default-audience', 'Home bakers');

      button('Choose from library').click();
      await settle();

      const search = root().querySelector<HTMLInputElement>('#cp-dam-picker-search')!;
      const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
      search.dispatchEvent(enter);
      await settle();

      expect(enter.defaultPrevented).toBeTrue();
      // No form of its own: the only form it sits in is the settings form, whose Save it must not trigger.
      expect(search.closest('form')).toBe(root().querySelector('form[aria-labelledby="brand-heading"]'));
      expect(spies.update).not.toHaveBeenCalled();
    });

    it('shows a viewer the logos and nothing to change them with', async () => {
      await create({ role: 'Viewer', read: { status: 'found', profile: profile({ assets: LOGOS }) } });

      expect(logoSection().textContent).toContain('Logo a1');
      expect(logoSection().querySelectorAll('button').length).toBe(0);
      expect(root().querySelector('cp-dam-asset-picker')).toBeNull();
    });

    it('keeps a logo the creator chose when someone else saved first', async () => {
      await create();
      await chooseFromLibrary('Choose from library', 'Wordmark');

      spies.update.and.resolveTo({ status: 'conflict' });
      spies.get.and.resolveTo({ status: 'found', profile: profile({ locale: 'fr-FR', revision: 2, concurrencyToken: 'tok-2' }) });
      await save();

      expect(logoSection().textContent).toContain('Logo a1');
      expect(spies.get).toHaveBeenCalledTimes(2);
    });
  });

  // ---- Leaving ----

  it('asks before leaving with unsaved edits and leaves when the creator agrees', async () => {
    await create();
    await type('brand-default-audience', 'Unsaved');

    spies.confirm.and.resolveTo(false);
    expect(await component().confirmDiscardIfDirty()).toBeFalse();
    expect(spies.confirm).toHaveBeenCalledTimes(1);

    spies.confirm.and.resolveTo(true);
    expect(await component().confirmDiscardIfDirty()).toBeTrue();
  });

  it('does not ask when nothing has changed', async () => {
    await create();

    expect(await component().confirmDiscardIfDirty()).toBeTrue();
    expect(spies.confirm).not.toHaveBeenCalled();
  });

  it('shows an unsaved-changes note while dirty', async () => {
    await create();
    expect(text()).not.toContain('unsaved changes');

    await type('brand-default-audience', 'Edit');

    expect(text()).toContain('You have unsaved changes.');
  });

  // ---- Accessibility semantics ----

  it('names every control and ties errors and hints to them', async () => {
    await create();

    expect(root().querySelector('h1')?.textContent).toContain('Brand settings');

    for (const control of Array.from(root().querySelectorAll<HTMLElement>('input, textarea, select'))) {
      if (control.id === '' || control.closest('cp-checkbox')) continue;
      const label = root().querySelector(`label[for="${control.id}"]`);
      expect(label).withContext(`#${control.id} needs a label`).toBeTruthy();
    }

    // The locale and time zone hints are wired to their controls, not just drawn near them.
    expect(input('brand-locale').getAttribute('aria-describedby')).toContain('brand-locale-hint');
    expect(input('brand-time-zone').getAttribute('aria-describedby')).toContain('brand-time-zone-hint');
  });

  it('labels each link row as a group and each remove button by its row', async () => {
    await create();

    const row = root().querySelector('li.row')!;
    expect(row.getAttribute('role')).toBe('group');
    expect(row.getAttribute('aria-label')).toBe('Link 1');
    expect(row.querySelector('button')?.getAttribute('aria-label')).toBe('Remove link 1');
  });

  it('announces errors and status through live regions', async () => {
    await create();
    spies.update.and.resolveTo({ status: 'unavailable' });
    await type('brand-default-audience', 'X');
    await save();

    expect(root().querySelector('.banner[role="alert"]')).toBeTruthy();
    expect(root().querySelector('cp-toast-region [aria-live]')).toBeTruthy();
  });
});
