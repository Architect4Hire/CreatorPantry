import { Component, signal, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpValidationIssue, CpValidationSummaryComponent } from './validation-summary.component';

@Component({
  standalone: true,
  imports: [CpValidationSummaryComponent],
  template: `<cp-validation-summary
    [issues]="issues()"
    [labelOf]="labelOf"
    [announce]="announce()"
    (issueSelected)="selected.push($event)"
  />`,
})
class HostComponent {
  readonly issues = signal<readonly CpValidationIssue[]>([]);
  readonly announce = signal(true);
  readonly selected: CpValidationIssue[] = [];
  readonly summary = viewChild.required(CpValidationSummaryComponent);
  readonly labelOf = (path: string) => (path === 'title' ? 'Title' : '');
}

const TITLE: CpValidationIssue = { path: 'title', message: 'Give the recipe a name.', severity: 'error' };
const YIELD: CpValidationIssue = { path: 'servings', message: 'That is a lot of servings.', severity: 'warning' };
const NOTE: CpValidationIssue = { path: 'notes', message: 'Notes are long.', severity: 'info' };

describe('CpValidationSummaryComponent', () => {
  function create() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    return {
      fixture,
      root,
      set(issues: readonly CpValidationIssue[]) {
        fixture.componentInstance.issues.set(issues);
        fixture.detectChanges();
      },
      status: () => root.querySelector('[role="status"]')!.textContent!.trim(),
      buttons: () => Array.from(root.querySelectorAll('button')),
    };
  }

  beforeEach(() => TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents());

  it('draws nothing but an empty status region when there are no issues', () => {
    const view = create();

    expect(view.root.querySelector('section')).toBeNull();
    expect(view.status()).toBe('');
  });

  it('lists every issue as a bulleted list of buttons, with no heading over it', () => {
    const view = create();

    view.set([TITLE, { ...TITLE, path: 'x', message: 'Other.' }]);

    expect(view.root.querySelector('h2')).toBeNull();
    expect(view.root.querySelectorAll('ul > li').length).toBe(2);
    expect(view.buttons().length).toBe(2);
    expect(view.root.querySelector('section')!.getAttribute('aria-label')).toBe('Problems with this form');
  });

  it('announces the number of errors, in the singular for one', () => {
    const view = create();

    view.set([TITLE]);
    expect(view.status()).toBe('1 problem to fix');

    view.set([TITLE, { ...TITLE, path: 'x', message: 'Other.' }]);
    expect(view.status()).toBe('2 problems to fix');
  });

  it('names the place an issue is about when the consumer can, and only then', () => {
    const view = create();

    view.set([TITLE, { ...TITLE, path: 'unlabelled', message: 'Nope.' }]);

    const [first, second] = view.buttons();
    expect(first.querySelector('.where')!.textContent).toBe('Title');
    expect(second.querySelector('.where')).toBeNull();
  });

  it('puts errors first and says advice is advice in a word, so it never differs by colour alone', () => {
    const view = create();

    view.set([YIELD, TITLE, NOTE]);

    const items = Array.from(view.root.querySelectorAll('li'));
    expect(items.map((item) => item.querySelector('.message')!.textContent)).toEqual([TITLE.message, YIELD.message, NOTE.message]);
    expect(items.map((item) => item.classList.contains('advice'))).toEqual([false, true, true]);
    expect(items[0].querySelector('.kind')).toBeNull();
    expect(items[1].querySelector('.kind')!.textContent).toBe('Heads up:');
    expect(items[2].querySelector('.kind')!.textContent).toBe('Note:');
  });

  it('does not announce advice: only errors are a refusal', () => {
    const view = create();

    view.set([YIELD]);

    expect(view.root.querySelectorAll('li').length).toBe(1);
    expect(view.status()).toBe('');
  });

  it('draws the box in the advice colour when nothing listed is an error, and in the error colour otherwise', () => {
    const view = create();

    view.set([YIELD]);
    expect(view.root.querySelector('section')!.classList.contains('advice-only')).toBeTrue();

    view.set([YIELD, TITLE]);
    expect(view.root.querySelector('section')!.classList.contains('advice-only')).toBeFalse();
  });

  it('draws each line as plain text: no underline and no fill on hover', () => {
    const view = create();
    document.body.appendChild(view.root);

    view.set([TITLE]);

    const line = getComputedStyle(view.buttons()[0]);
    expect(line.textDecorationLine).toBe('none');
    expect(line.backgroundColor).toBe('rgba(0, 0, 0, 0)');
    view.root.remove();
  });

  it('emits the issue chosen and does nothing else', () => {
    const view = create();
    view.set([TITLE, YIELD]);

    view.buttons()[1].click();

    expect(view.fixture.componentInstance.selected).toEqual([YIELD]);
  });

  it('moves focus to the list on request', () => {
    const view = create();
    document.body.appendChild(view.root);
    view.set([TITLE]);

    view.fixture.componentInstance.summary().focus();

    expect(document.activeElement).toBe(view.root.querySelector('section'));
    view.root.remove();
  });

  it('waits for the list to be drawn when asked to focus it in the same breath as the issues are set', async () => {
    const view = create();
    document.body.appendChild(view.root);

    view.fixture.componentInstance.summary().focus();
    view.fixture.componentInstance.issues.set([TITLE]);
    view.fixture.detectChanges();
    await view.fixture.whenStable();

    expect(document.activeElement).toBe(view.root.querySelector('section'));
    view.root.remove();
  });

  it('does nothing when the render that follows still has nothing to show', async () => {
    const view = create();

    view.fixture.componentInstance.summary().focus();
    view.fixture.detectChanges();
    await view.fixture.whenStable();

    expect(view.root.querySelector('section')).toBeNull();
  });

  it('keeps no status region when the form announces a refusal itself, and still lists the issues', () => {
    const view = create();
    view.fixture.componentInstance.announce.set(false);

    view.set([TITLE]);

    expect(view.root.querySelector('[role="status"]')).toBeNull();
    expect(view.root.querySelectorAll('li').length).toBe(1);
  });

  it('keeps every button at least 40px tall', () => {
    const view = create();
    document.body.appendChild(view.root);

    view.set([TITLE]);

    expect(view.buttons()[0].getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
    view.root.remove();
  });
});
