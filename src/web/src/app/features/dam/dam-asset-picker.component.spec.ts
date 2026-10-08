import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';

import { VisibilityService } from '../../core/visibility.service';
import { DamAssetSearchQuery, DamAssetSummary } from '../../models/dam-asset.models';
import { DamAssetSearchOutcome, DamAssetService } from '../../services/dam-asset.service';
import { assetSummary } from '../recipes/recipe-media.testing';
import { DamAssetPickerComponent } from './dam-asset-picker.component';

@Component({
  standalone: true,
  imports: [DamAssetPickerComponent],
  template: `
    <form (submit)="outerSubmits = outerSubmits + 1">
      <cp-dam-asset-picker [workspaceSlug]="slug()" [active]="active()" (chosen)="chosen.push($event)" />
    </form>
  `,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly active = signal(true);
  readonly chosen: DamAssetSummary[] = [];
  readonly picker = viewChild.required(DamAssetPickerComponent);
  outerSubmits = 0;
}

const delay = (ms = 0): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamAssetPickerComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let searchSpy: jasmine.Spy<(slug: string, query: DamAssetSearchQuery) => Observable<DamAssetSearchOutcome>>;

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

  function search(): HTMLInputElement {
    return root().querySelector<HTMLInputElement>('#cp-dam-picker-search')!;
  }

  function found(items: DamAssetSummary[], nextCursor: string | null = null, totalCount: number | null = items.length) {
    return of<DamAssetSearchOutcome>({ status: 'found', page: { items, nextCursor, totalCount } });
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    await delay();
    fixture.detectChanges();
  }

  async function create(): Promise<void> {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await settle();
  }

  beforeEach(async () => {
    searchSpy = jasmine.createSpy('search').and.callFake(() => found([assetSummary()]));

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        { provide: DamAssetService, useValue: { search: searchSpy, content: () => of({ status: 'gone' }) } },
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
      ],
    }).compileComponents();
  });

  it('searches the library, newest first, when it becomes active', async () => {
    await create();

    expect(searchSpy).toHaveBeenCalledOnceWith('cozy-fall', {
      search: '',
      channel: null,
      day: null,
      sort: 'RecentlyAdded',
      cursor: null,
    });
    expect(text()).toContain('Soda bread hero');
  });

  it('does nothing while inactive, and starts afresh each time it is activated', async () => {
    TestBed.overrideComponent(HostComponent, { set: {} });
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.active.set(false);
    await settle();

    expect(searchSpy).not.toHaveBeenCalled();

    host.active.set(true);
    await settle();
    search().value = 'loaf';
    search().dispatchEvent(new Event('input'));
    host.active.set(false);
    await settle();
    host.active.set(true);
    await settle();

    // The second activation is a new look at the library, not the last search replayed.
    expect(searchSpy.calls.mostRecent().args[1].search).toBe('');
    expect(search().value).toBe('');
  });

  it('hands the chosen picture to its host and writes nothing itself', async () => {
    await create();

    button('Choose Soda bread hero').click();

    expect(host.chosen.map((asset) => asset.id)).toEqual(['a1']);
    expect(searchSpy).toHaveBeenCalledTimes(1);
  });

  it('searches after a pause in typing, once, with the latest words', async () => {
    await create();

    for (const value of ['l', 'lo', 'loaf']) {
      search().value = value;
      search().dispatchEvent(new Event('input'));
    }
    await delay(380);
    await settle();

    expect(searchSpy).toHaveBeenCalledTimes(2);
    expect(searchSpy.calls.mostRecent().args[1].search).toBe('loaf');
  });

  it('searches at once on Enter, without submitting a form it sits inside', async () => {
    await create();

    search().value = 'loaf';
    search().dispatchEvent(new Event('input'));
    const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    search().dispatchEvent(enter);
    await settle();

    expect(searchSpy.calls.mostRecent().args[1].search).toBe('loaf');
    expect(enter.defaultPrevented).toBeTrue();
    expect(host.outerSubmits).toBe(0);
    // A landmark, not a nested form: the only form around the field is the host's.
    expect(root().querySelector('[role="search"]')?.tagName).toBe('DIV');
    expect(root().querySelectorAll('form').length).toBe(1);
  });

  it('follows the cursor on Load more, appends, and shows a picture that arrives twice once', async () => {
    searchSpy.and.callFake((_slug, query) =>
      query.cursor === null
        ? found([assetSummary()], 'c2', 3)
        : found([assetSummary(), assetSummary({ id: 'a2', title: 'Crumb close-up' })], null, null),
    );
    await create();

    button('Load more').click();
    await settle();

    expect(searchSpy.calls.mostRecent().args[1].cursor).toBe('c2');
    expect(root().querySelectorAll('li.tile').length).toBe(2);
    expect(text()).toContain('2 of 3 pictures shown.');
    expect(root().querySelector('button[disabled]')).toBeNull();
  });

  it('keeps what is shown when a further page fails, and says so', async () => {
    searchSpy.and.callFake((_slug, query) =>
      query.cursor === null ? found([assetSummary()], 'c2', 3) : of<DamAssetSearchOutcome>({ status: 'unavailable' }),
    );
    await create();

    button('Load more').click();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('More pictures could not be loaded.');
    expect(text()).toContain('Soda bread hero');
    expect(button('Load more').disabled).toBeFalse();
  });

  it('starts the list again by itself when the server no longer honours the cursor', async () => {
    let first = true;
    searchSpy.and.callFake((_slug, query) => {
      if (query.cursor !== null) return of<DamAssetSearchOutcome>({ status: 'cursor_expired' });
      const page = first ? found([assetSummary()], 'c2', 2) : found([assetSummary({ id: 'a7', title: 'Fresh start' })]);
      first = false;
      return page;
    });
    await create();

    button('Load more').click();
    await settle();

    expect(searchSpy.calls.allArgs().map((args) => args[1].cursor)).toEqual([null, 'c2', null]);
    expect(text()).toContain('Fresh start');
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('drops a page that answers a search the creator has since replaced', async () => {
    const slow = new Subject<DamAssetSearchOutcome>();
    searchSpy.and.callFake((_slug, query) => (query.cursor === null ? found([assetSummary()], 'c2', 2) : slow));
    await create();

    button('Load more').click();
    await settle();

    searchSpy.and.callFake(() => found([assetSummary({ id: 'a5', title: 'Newer search' })]));
    search().value = 'newer';
    search().dispatchEvent(new Event('input'));
    search().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await settle();

    slow.next({ status: 'found', page: { items: [assetSummary({ id: 'a2', title: 'Late arrival' })], nextCursor: null, totalCount: 2 } });
    await settle();

    expect(text()).toContain('Newer search');
    expect(text()).not.toContain('Late arrival');
  });

  it('offers a retry when the library cannot be loaded', async () => {
    searchSpy.and.returnValue(of<DamAssetSearchOutcome>({ status: 'unavailable' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('The library could not be loaded.');

    searchSpy.and.callFake(() => found([assetSummary()]));
    button('Try again').click();
    await settle();

    expect(text()).toContain('Soda bread hero');
  });

  it('says an empty library and an empty search differently', async () => {
    searchSpy.and.callFake(() => found([]));
    await create();

    expect(text()).toContain('The library has no pictures yet');

    search().value = 'zzz';
    search().dispatchEvent(new Event('input'));
    search().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await settle();

    expect(text()).toContain('No pictures match that search');

    button('Clear the search').click();
    await settle();

    expect(searchSpy.calls.mostRecent().args[1].search).toBe('');
  });

  it('labels the search, names each Choose by its picture, and announces the count', async () => {
    await create();

    expect(root().querySelector('label[for="cp-dam-picker-search"]')?.textContent).toContain('Search the library');
    expect(button('Choose Soda bread hero').textContent?.trim()).toBe('Choose');
    expect(root().querySelector('[role="status"].cp-sr-only')?.textContent).toContain('1 of 1 pictures shown.');
    expect(root().querySelector('ul[aria-label="Library pictures"]')).not.toBeNull();
  });

  it('offers nothing that would change the library', async () => {
    await create();

    const labels = Array.from(root().querySelectorAll('button, a')).map((each) => (each.textContent ?? '').trim().toLowerCase());

    expect(labels.some((label) => /upload|edit|delete|remove/.test(label))).toBeFalse();
    expect(root().querySelector('input[type="file"]')).toBeNull();
  });

  it('returns focus to the card of an earlier choice, or to the search when that card is gone', async () => {
    await create();

    host.picker().focusChoice('a1');
    await settle();
    expect(document.activeElement).toBe(button('Choose Soda bread hero'));

    host.picker().focusChoice('not-shown');
    await settle();
    expect(document.activeElement).toBe(search());
  });
});
