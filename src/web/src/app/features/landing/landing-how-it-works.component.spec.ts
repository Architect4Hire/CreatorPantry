import { TestBed } from '@angular/core/testing';

import { DEFAULT_HOW_IT_WORKS_STEPS, LandingHowItWorksComponent } from './landing-how-it-works.component';

describe('LandingHowItWorksComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingHowItWorksComponent],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingHowItWorksComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the four default steps in order', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const headings = el.querySelectorAll('.steps h3');

    expect(headings.length).toBe(DEFAULT_HOW_IT_WORKS_STEPS.length);
    headings.forEach((heading, i) => expect(heading.textContent).toContain(DEFAULT_HOW_IT_WORKS_STEPS[i].heading));
  });

  it('never describes AI output as auto-applied or publishing as automatic', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const text = el.textContent ?? '';

    expect(text).toContain('never a change applied to your recipe or content on its own');
    expect(text).toContain('one explicit confirmation');
    expect(text).not.toMatch(/automatically publish|auto-publish|applies (the )?changes? automatically/i);
  });

  it('renders the in-development caveat note by default', async () => {
    const fixture = await createFixture();
    const note = fixture.nativeElement.querySelector('.note');

    expect(note?.textContent).toContain('in active development');
  });

  it('omits the note element when note is cleared', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('note', '');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.note')).toBeFalsy();
  });

  it('renders custom steps supplied via input', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('steps', [{ heading: 'Custom step', body: 'Custom body' }]);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('.steps > li').length).toBe(1);
    expect(el.querySelector('.steps h3')?.textContent).toContain('Custom step');
  });

  it("labels the section by its own heading, giving it an accessible name", async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const section = el.querySelector('section')!;
    const heading = el.querySelector('h2')!;

    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(heading.id).toBeTruthy();
  });

  it('uses ordered-list semantics with decorative, non-redundant step numbers', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const list = el.querySelector('ol.steps')!;
    const stepNumbers = el.querySelectorAll('.step-number');

    expect(list.getAttribute('role')).toBe('list');
    expect(list.querySelectorAll(':scope > li').length).toBe(DEFAULT_HOW_IT_WORKS_STEPS.length);
    stepNumbers.forEach((num) => expect(num.getAttribute('aria-hidden')).toBe('true'));
  });
});
