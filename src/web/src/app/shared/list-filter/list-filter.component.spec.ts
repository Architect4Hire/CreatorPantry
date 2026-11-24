import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LIST_PREVIEW_ROWS, matchesWords, searchWords } from './list-filter';
import { ListFilterComponent } from './list-filter.component';

@Component({
  selector: 'cp-test-host',
  standalone: true,
  imports: [ListFilterComponent],
  template: `
    <cp-list-filter
      label="Search your recipes"
      placeholder="Search, such as “loaf”"
      [(query)]="query"
      [status]="status()"
      [noMatch]="noMatch()"
      noMatchTitle="No recipe matches that"
    />
  `,
})
class HostComponent {
  readonly query = signal('');
  readonly status = signal('');
  readonly noMatch = signal(false);
}

describe('searchWords and matchesWords', () => {
  it('splits what was typed into lower-cased words, and ignores spaces around and between them', () => {
    expect(searchWords('  Rye   LOAF ')).toEqual(['rye', 'loaf']);
    expect(searchWords('   ')).toEqual([]);
  });

  it('matches when every word is somewhere in the text, in any order, whatever the case', () => {
    expect(matchesWords('Banana bread — Version 4', searchWords('bread banana'))).toBeTrue();
    expect(matchesWords('Banana bread — Version 4', searchWords('bread rye'))).toBeFalse();
  });

  it('matches everything when nothing is typed, because no filter is not a filter for nothing', () => {
    expect(matchesWords('anything', [])).toBeTrue();
  });

  it('previews five, the number every filtered list starts from', () => {
    expect(LIST_PREVIEW_ROWS).toBe(5);
  });
});

describe('ListFilterComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  const root = () => fixture.nativeElement as HTMLElement;
  const input = () => root().querySelector<HTMLInputElement>('input[type="search"]')!;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('names the box by what it searches, in a real label, and shows the placeholder', () => {
    expect(input().labels?.[0]?.textContent?.trim()).toBe('Search your recipes');
    expect(input().placeholder).toBe('Search, such as “loaf”');
  });

  it('writes what is typed to the page’s own signal, and shows what the page sets', () => {
    input().value = 'rye';
    input().dispatchEvent(new Event('input'));
    expect(host.query()).toBe('rye');

    host.query.set('banana');
    fixture.detectChanges();
    expect(input().value).toBe('banana');
  });

  it('keeps one status region present, so a result is announced when it changes — including nothing', () => {
    const status = () => root().querySelector('[role="status"]')!;
    expect(status().textContent?.trim()).toBe('');

    host.status.set('4 of 8 shown.');
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe('4 of 8 shown.');
  });

  it('draws the no-results state only when nothing matched, and its Clear search empties the box', () => {
    expect(root().querySelector('cp-empty-state')).toBeNull();

    host.query.set('spaceship');
    host.noMatch.set(true);
    fixture.detectChanges();

    const empty = root().querySelector('cp-empty-state')!;
    expect(empty.textContent).toContain('No recipe matches that');

    Array.from(empty.querySelectorAll('button'))
      .find((button) => button.textContent?.trim() === 'Clear search')!
      .click();
    fixture.detectChanges();

    expect(host.query()).toBe('');
    expect(input().value).toBe('');
  });
});
