import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';

import { DamAssetUtilization, DamAssetUtilizationPage } from '../../models/dam-asset.models';
import { DamAssetService, DamAssetUtilizationOutcome } from '../../services/dam-asset.service';
import { DamAssetUsageHistoryComponent } from './dam-asset-usage-history.component';

function use(id: string, overrides: Partial<DamAssetUtilization> = {}): DamAssetUtilization {
  return {
    id,
    platformKey: 'instagram',
    utilizedOn: '2026-10-06',
    utilizedDay: 'Tuesday',
    campaignName: null,
    notes: null,
    ...overrides,
  };
}

function page(items: readonly DamAssetUtilization[], overrides: Partial<DamAssetUtilizationPage> = {}): DamAssetUtilizationPage {
  return { items, nextCursor: null, totalCount: items.length, ...overrides };
}

function found(usagePage: DamAssetUtilizationPage): Observable<DamAssetUtilizationOutcome> {
  return of<DamAssetUtilizationOutcome>({ status: 'found', page: usagePage });
}

@Component({
  standalone: true,
  imports: [DamAssetUsageHistoryComponent],
  template: `<cp-dam-asset-usage-history [workspaceSlug]="slug()" [assetId]="assetId()" />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly assetId = signal('a1');
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamAssetUsageHistoryComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let spy: jasmine.Spy<(slug: string, id: string, cursor: string | null) => Observable<DamAssetUtilizationOutcome>>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function rows(): HTMLElement[] {
    return Array.from(root().querySelectorAll<HTMLElement>('tbody tr'));
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function hasButton(label: string): boolean {
    return Array.from(root().querySelectorAll('button')).some((each) => each.textContent?.includes(label));
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  async function create(): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [{ provide: DamAssetService, useValue: { utilization: spy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    await settle();
  }

  it('says it is loading, and asks for the first page of this asset in this workspace', async () => {
    spy = jasmine.createSpy('utilization').and.returnValue(new Subject<DamAssetUtilizationOutcome>());
    await create();

    expect(spy).toHaveBeenCalledOnceWith('cozy-fall', 'a1', null);
    expect(root().querySelector('[role="status"]')?.textContent).toContain('Loading where this has been used');
  });

  it('lists each use with its date, weekday, platform, campaign and notes', async () => {
    spy = jasmine.createSpy('utilization').and.returnValue(
      found(
        page([
          use('u1', { campaignName: 'Autumn bakes', notes: 'Carousel, slide one.' }),
          use('u2', { utilizedOn: '2026-09-30', utilizedDay: 'Wednesday', platformKey: 'newsletter' }),
        ]),
      ),
    );
    await create();

    expect(rows().length).toBe(2);
    expect(rows()[0].textContent).toContain('Oct 6, 2026');
    expect(rows()[0].textContent).toContain('Tuesday');
    expect(rows()[0].textContent).toContain('instagram');
    expect(rows()[0].textContent).toContain('Autumn bakes');
    expect(rows()[0].textContent).toContain('Carousel, slide one.');
    expect(rows()[1].textContent).toContain('Sep 30, 2026');
    expect(rows()[1].textContent).toContain('newsletter');
    expect(text()).toContain('Showing 2 of 2, newest first.');
  });

  it("shows the server's weekday as sent, even where it disagrees with what this browser would derive", async () => {
    // 6 October 2026 is a Tuesday. A row saying Monday is the server's fact about the workspace's own zone,
    // and re-deriving it here would overwrite that with this browser's calendar.
    spy = jasmine.createSpy('utilization').and.returnValue(found(page([use('u1', { utilizedDay: 'Monday' })])));
    await create();

    expect(rows()[0].textContent).toContain('Oct 6, 2026');
    expect(rows()[0].textContent).toContain('Monday');
    expect(rows()[0].textContent).not.toContain('Tuesday');
  });

  it('says so plainly when the asset has never been used, with no table and nothing to load', async () => {
    spy = jasmine.createSpy('utilization').and.returnValue(found(page([])));
    await create();

    expect(text()).toContain('Not used yet');
    expect(root().querySelector('table')).toBeNull();
    expect(hasButton('Load more')).toBeFalse();
  });

  it('reports a failed first read with a retry that reads again', async () => {
    spy = jasmine.createSpy('utilization').and.returnValue(of<DamAssetUtilizationOutcome>({ status: 'unavailable' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");

    spy.and.returnValue(found(page([use('u1')])));
    button('Try again').click();
    await settle();

    expect(rows().length).toBe(1);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('says the history cannot be shown when the asset has gone, with nothing to retry', async () => {
    spy = jasmine.createSpy('utilization').and.returnValue(of<DamAssetUtilizationOutcome>({ status: 'not_found' }));
    await create();

    expect(text()).toContain("can't be shown");
    expect(root().querySelector('button')).toBeNull();
  });

  it("appends the next page by following the server's cursor, and moves focus to the first new row", async () => {
    spy = jasmine
      .createSpy('utilization')
      .and.returnValues(
        found(page([use('u1'), use('u2')], { nextCursor: 'c1', totalCount: 3 })),
        found(page([use('u3')], { nextCursor: null, totalCount: null })),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(spy.calls.mostRecent().args).toEqual(['cozy-fall', 'a1', 'c1']);
    expect(rows().map((row) => row.getAttribute('data-use-id'))).toEqual(['u1', 'u2', 'u3']);
    expect(text()).toContain('Showing 3 of 3, newest first.');
    expect(hasButton('Load more')).toBeFalse();
    expect(document.activeElement).toBe(rows()[2].querySelector('th'));
  });

  it('keeps the rows already shown when a later page fails, and lets the creator try again', async () => {
    spy = jasmine
      .createSpy('utilization')
      .and.returnValues(
        found(page([use('u1')], { nextCursor: 'c1', totalCount: 2 })),
        of<DamAssetUtilizationOutcome>({ status: 'unavailable' }),
        found(page([use('u2')])),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(rows().length).toBe(1);
    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load more of the history");

    button('Try loading more again').click();
    await settle();

    expect(rows().length).toBe(2);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('reads the history again from the start when the server says the cursor is stale', async () => {
    spy = jasmine
      .createSpy('utilization')
      .and.returnValues(
        found(page([use('u1')], { nextCursor: 'stale', totalCount: 2 })),
        of<DamAssetUtilizationOutcome>({ status: 'cursor_expired' }),
        found(page([use('u9')])),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(spy.calls.allArgs().map((args) => args[2])).toEqual([null, 'stale', null]);
    expect(rows().map((row) => row.getAttribute('data-use-id'))).toEqual(['u9']);
  });

  it("shows nothing of one workspace's history, and sends none of its cursor, when the workspace changes", async () => {
    const other = new Subject<DamAssetUtilizationOutcome>();
    spy = jasmine.createSpy('utilization').and.callFake((slug: string) =>
      slug === 'cozy-fall'
        ? found(page([use('u1', { campaignName: 'Workspace A campaign' })], { nextCursor: 'cursor-of-a', totalCount: 5 }))
        : other,
    );
    await create();
    expect(text()).toContain('Workspace A campaign');

    fixture.componentInstance.slug.set('other-kitchen');
    await settle();

    expect(rows().length).toBe(0);
    expect(text()).not.toContain('Workspace A campaign');
    expect(hasButton('Load more')).toBeFalse();
    expect(spy.calls.mostRecent().args).toEqual(['other-kitchen', 'a1', null]);

    other.next({ status: 'found', page: page([use('b1', { campaignName: 'Workspace B campaign' })]) });
    await settle();

    expect(text()).toContain('Workspace B campaign');
    expect(spy.calls.allArgs().filter((args) => args[0] === 'other-kitchen' && args[2] !== null)).toEqual([]);
  });

  it('drops an answer that arrives for an asset the page has moved on from', async () => {
    const slow = new Subject<DamAssetUtilizationOutcome>();
    spy = jasmine.createSpy('utilization').and.callFake((_slug: string, id: string) =>
      id === 'a1' ? slow : found(page([use('second')])),
    );
    await create();

    fixture.componentInstance.assetId.set('a2');
    await settle();
    slow.next({ status: 'found', page: page([use('first')]) });
    await settle();

    expect(rows().map((row) => row.getAttribute('data-use-id'))).toEqual(['second']);
  });

  it('is a captioned table with column and row headers, in a keyboard-reachable scrolling region', async () => {
    spy = jasmine.createSpy('utilization').and.returnValue(found(page([use('u1')])));
    await create();

    expect(root().querySelector('caption')?.textContent).toContain('newest first');
    expect(Array.from(root().querySelectorAll('thead th')).every((th) => th.getAttribute('scope') === 'col')).toBeTrue();
    expect(rows()[0].querySelector('th')?.getAttribute('scope')).toBe('row');

    const region = root().querySelector('[role="region"]') as HTMLElement;
    expect(region.tabIndex).toBe(0);
    expect(region.getAttribute('aria-label')).toBe('Usage history');

    // An empty cell is said rather than left blank.
    expect(rows()[0].textContent).toContain('None');
  });
});
