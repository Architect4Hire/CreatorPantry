import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { VisibilityService } from '../../core/visibility.service';
import { BRAND_LIMITS, BrandAsset } from '../../models/brand-profile.models';
import { DamAssetDetailOutcome, DamAssetSearchOutcome, DamAssetService } from '../../services/dam-asset.service';
import { assetDetail, assetSummary } from '../recipes/recipe-media.testing';
import { BrandLogosComponent } from './brand-logos.component';

@Component({
  standalone: true,
  imports: [BrandLogosComponent],
  template: `
    <cp-brand-logos
      workspaceSlug="sams-kitchen"
      [assets]="assets()"
      [canEdit]="canEdit()"
      [refusedIds]="refused()"
      refusedMessage="That picture is not in this workspace's library."
      (changed)="onChanged($event)"
    />
  `,
})
class HostComponent {
  readonly assets = signal<readonly BrandAsset[]>([]);
  readonly canEdit = signal(true);
  readonly refused = signal<readonly string[]>([]);
  readonly changes: (readonly BrandAsset[])[] = [];

  onChanged(next: readonly BrandAsset[]): void {
    this.changes.push(next);
    this.assets.set(next);
  }
}

const PRIMARY: BrandAsset = { mediaAssetId: 'a1', role: 'PrimaryLogo' };
const MARK: BrandAsset = { mediaAssetId: 'a2', role: 'AlternateLogo' };

const delay = (ms = 0): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('BrandLogosComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return (root().textContent ?? '').replace(/\s+/g, ' ');
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find(
      (each) => each.textContent?.trim().startsWith(label) || each.getAttribute('aria-label') === label,
    );
    if (!match) throw new Error(`no button "${label}"`);
    return match;
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    await delay();
    fixture.detectChanges();
  }

  async function create(configure: (host: HostComponent) => void = () => undefined): Promise<void> {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    configure(host);
    await settle();
  }

  async function choose(opener: string, title: string): Promise<void> {
    button(opener).click();
    await settle();
    button(`Choose ${title}`).click();
    await settle();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([]),
        {
          provide: DamAssetService,
          useValue: {
            detail: (_slug: string, id: string) =>
              of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({ id, title: `Logo ${id}` }) }),
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
  });

  it('says there is no primary logo and offers to choose one', async () => {
    await create();

    expect(text()).toContain('No primary logo chosen.');
    expect(button('Choose from library').disabled).toBeFalse();
    expect(root().querySelector('[role="dialog"]')).toBeNull();
  });

  it('chooses a primary logo through the library and hands the new list to the form', async () => {
    await create();

    await choose('Choose from library', 'Wordmark');

    expect(host.changes).toEqual([[PRIMARY]]);
    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(text()).toContain('Logo a1');
    // Focus returns to the control that now stands where the opener was.
    expect(document.activeElement).toBe(button('Change'));
  });

  it('replaces the primary logo on Change rather than keeping the old one', async () => {
    await create((h) => h.assets.set([{ mediaAssetId: 'a9', role: 'PrimaryLogo' }, MARK]));

    await choose('Change', 'Wordmark');

    expect(host.changes).toEqual([[PRIMARY, MARK]]);
  });

  it('promotes an alternate chosen as the primary instead of listing it twice', async () => {
    await create((h) => h.assets.set([{ mediaAssetId: 'a9', role: 'PrimaryLogo' }, MARK]));

    await choose('Change', 'Mark only');

    expect(host.changes).toEqual([[{ mediaAssetId: 'a2', role: 'PrimaryLogo' }]]);
  });

  it('adds another logo at the end', async () => {
    await create((h) => h.assets.set([PRIMARY]));

    await choose('Add from library', 'Mark only');

    expect(host.changes).toEqual([[PRIMARY, MARK]]);
    expect(document.activeElement).toBe(button('Add from library'));
  });

  it('says so, and changes nothing, when the chosen picture is already a logo', async () => {
    await create((h) => h.assets.set([PRIMARY]));

    await choose('Add from library', 'Wordmark');

    expect(host.changes).toEqual([]);
    expect(root().querySelector('.logos > cp-notice[role="status"]')?.textContent).toContain('Wordmark is already one of this brand’s logos.');
  });

  it('unlinks without asking, since nothing is saved until the form is, and moves focus somewhere useful', async () => {
    await create((h) => h.assets.set([PRIMARY, MARK]));

    button('Unlink other logo 1').click();
    await settle();
    expect(host.changes).toEqual([[PRIMARY]]);
    expect(document.activeElement).toBe(button('Add from library'));

    button('Unlink the primary logo').click();
    await settle();
    expect(host.changes[1]).toEqual([]);
    expect(document.activeElement).toBe(button('Choose from library'));
  });

  it('stops adding at the limit and says why on the disabled button', async () => {
    const full: BrandAsset[] = Array.from({ length: BRAND_LIMITS.maxLogos }, (_, index) => ({
      mediaAssetId: `x${index}`,
      role: index === 0 ? 'PrimaryLogo' : 'AlternateLogo',
    }));
    await create((h) => h.assets.set(full));

    const add = button('Add from library');
    expect(add.disabled).toBeTrue();
    expect(root().querySelector(`[id="${add.getAttribute('aria-describedby')}"]`)?.textContent).toContain(
      `A brand can keep up to ${BRAND_LIMITS.maxLogos} logos.`,
    );
  });

  it('marks only the logo that was refused, as an alert on its own row', async () => {
    await create((h) => {
      h.assets.set([PRIMARY, MARK]);
      h.refused.set(['a2']);
    });

    expect(root().querySelector('[data-logo-id="a2"] [role="alert"]')?.textContent).toContain(
      "That picture is not in this workspace's library. Unlink it or choose another.",
    );
    expect(root().querySelector('[data-logo-id="a1"] [role="alert"]')).toBeNull();
  });

  it('shows the logos and no way to change them without edit access', async () => {
    await create((h) => {
      h.assets.set([PRIMARY, MARK]);
      h.canEdit.set(false);
    });

    expect(text()).toContain('Logo a1');
    expect(text()).toContain('Logo a2');
    expect(root().querySelectorAll('button').length).toBe(0);
    expect(root().querySelector('cp-dialog')).toBeNull();
  });

  it('closes the library on Cancel and on Escape, changing nothing and returning focus to the opener', async () => {
    await create();

    button('Choose from library').click();
    await settle();
    button('Cancel').click();
    await settle();

    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(button('Choose from library'));

    button('Choose from library').click();
    await settle();
    root().querySelector('cp-dialog')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle();

    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(host.changes).toEqual([]);
  });

  it('names the two groups, the dialog by what it is choosing, and each Unlink by its logo', async () => {
    await create((h) => h.assets.set([PRIMARY, MARK]));

    const groups = Array.from(root().querySelectorAll('[role="group"]')).map(
      (group) => root().querySelector(`[id="${group.getAttribute('aria-labelledby')}"]`)?.textContent?.trim(),
    );
    expect(groups).toEqual(['Primary logo', 'Other logos']);
    expect(button('Unlink the primary logo').textContent?.trim()).toBe('Unlink');

    button('Add from library').click();
    await settle();

    const dialog = root().querySelector('[role="dialog"]')!;
    expect(root().querySelector(`[id="${dialog.getAttribute('aria-labelledby')}"]`)?.textContent).toBe(
      'Add another logo from the library',
    );
  });

  it('never says a picture is deleted or removed, and offers nothing that would change the library', async () => {
    await create((h) => h.assets.set([PRIMARY, MARK]));

    const labels = Array.from(root().querySelectorAll('button')).map((each) =>
      `${each.textContent ?? ''} ${each.getAttribute('aria-label') ?? ''}`.toLowerCase(),
    );

    expect(text().toLowerCase()).not.toMatch(/delete|remove/);
    expect(labels.some((label) => /upload|edit|delete|remove/.test(label))).toBeFalse();
    expect(text()).toContain('Unlinking one doesn’t change the picture in your library.');
  });
});
