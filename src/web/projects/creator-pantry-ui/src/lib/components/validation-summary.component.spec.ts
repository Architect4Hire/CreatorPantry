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
      headings: () => Array.from(root.querySelectorAll('h2')).map((h) => h.textContent!.trim()),
    };
  }

  beforeEach(() => TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents());

  it('draws nothing but an empty status region when there are no issues', () => {
    const view = create();

    expect(view.root.querySelector('section')).toBeNull();
    expect(view.status()).toBe('');
  });

  it('heads the errors with their count and lists each as a button', () => {
    const view = create();

    view.set([TITLE, { ...TITLE, path: 'x', message: 'Other.' }]);

    expect(view.headings()).toEqual(['2 problems to fix']);
    expect(view.buttons().length).toBe(2);
    expect(view.status()).toBe('2 problems to fix');
  });

  it('says "1 problem" in the singular', () => {
    const view = create();

    view.set([TITLE]);

    expect(view.headings()).toEqual(['1 problem to fix']);
  });

  it('names the place an issue is about when the consumer can, and only then', () => {
    const view = create();

    view.set([TITLE, { ...TITLE, path: 'unlabelled', message: 'Nope.' }]);

    const [first, second] = view.buttons();
    expect(first.querySelector('.where')!.textContent).toBe('Title');
    expect(second.querySelector('.where')).toBeNull();
  });

  it('puts warnings and information under their own heading, announced as advice and not as a refusal', () => {
    const view = create();

    view.set([TITLE, YIELD, NOTE]);

    expect(view.headings()).toEqual(['1 problem to fix', '2 things worth a look']);
    expect(view.status()).toBe('1 problem to fix');
    expect(view.buttons()[1].textContent).toContain('Heads up:');
    expect(view.buttons()[2].textContent).toContain('Note:');
  });

  it('is not a refusal when there is only advice: no error styling and nothing announced', () => {
    const view = create();

    view.set([YIELD]);

    expect(view.headings()).toEqual(['1 thing worth a look']);
    expect(view.root.querySelector('section')!.classList.contains('has-errors')).toBeFalse();
    expect(view.status()).toBe('');
    expect(view.root.querySelector('section')!.getAttribute('aria-labelledby')).toBe(view.root.querySelector('h2')!.id);
  });

  it('gives every severity a glyph that is hidden from assistive technology', () => {
    const view = create();

    view.set([TITLE, YIELD, NOTE]);

    const glyphs = Array.from(view.root.querySelectorAll('.glyph'));
    expect(glyphs.map((glyph) => glyph.textContent)).toEqual(['!', '▲', 'ℹ']);
    expect(glyphs.every((glyph) => glyph.getAttribute('aria-hidden') === 'true')).toBeTrue();
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
    expect(view.headings()).toEqual(['1 problem to fix']);
  });

  it('keeps every button at least 40px tall', () => {
    const view = create();
    document.body.appendChild(view.root);

    view.set([TITLE]);

    expect(view.buttons()[0].getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
    view.root.remove();
  });
});
