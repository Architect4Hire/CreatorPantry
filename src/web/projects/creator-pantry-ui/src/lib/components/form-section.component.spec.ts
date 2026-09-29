import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpFieldComponent } from './field.component';
import { CpFormSectionComponent } from './form-section.component';

@Component({
  standalone: true,
  imports: [CpFormSectionComponent, CpFieldComponent],
  template: `
    <cp-form-section [heading]="heading" [intro]="intro" [problem]="problem" [sectionId]="sectionId">
      <cp-field label="Title" forId="title-control">
        <input id="title-control" type="text" />
      </cp-field>
    </cp-form-section>
  `,
})
class HostComponent {
  heading = 'Details';
  intro = '';
  problem = '';
  sectionId = '';
}

@Component({
  standalone: true,
  imports: [CpFormSectionComponent],
  template: `
    <cp-form-section heading="Details" />
    <cp-form-section heading="Timing" />
  `,
})
class TwoSectionHostComponent {}

describe('CpFormSectionComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    return TestBed.createComponent(HostComponent);
  }

  it('names its region from its own heading', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const section = host.querySelector('section')!;
    const headingId = section.getAttribute('aria-labelledby')!;

    expect(host.querySelector(`#${headingId}`)?.textContent).toContain('Details');
    expect(host.querySelector(`#${headingId}`)?.tagName).toBe('H2');
  });

  it('leaves out the intro entirely rather than rendering an empty paragraph', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.intro')).toBeNull();
  });

  it('renders the intro under the heading when it has one', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.intro = 'The essentials creators see first.';
    fixture.detectChanges();

    const section = (fixture.nativeElement as HTMLElement).querySelector('section')!;
    const order = Array.from(section.children).map((child) => child.className);
    expect(section.querySelector('.intro')?.textContent).toContain('essentials');
    expect(order.indexOf('intro')).toBeGreaterThan(order.indexOf('heading'));
  });

  it('announces a problem and describes the section by it, so focusing here reads the reason', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.problem = 'Line 3 has no quantity.';
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const section = host.querySelector('section')!;
    const problem = host.querySelector('.problem')!;

    expect(problem.getAttribute('role')).toBe('alert');
    expect(problem.textContent).toContain('Line 3 has no quantity.');
    expect(section.getAttribute('aria-describedby')).toBe(problem.id);
    expect(problem.id.length).toBeGreaterThan(0);
  });

  it('describes the section by nothing at all when there is no problem', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const section = (fixture.nativeElement as HTMLElement).querySelector('section')!;
    expect(section.getAttribute('aria-describedby')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('.problem')).toBeNull();
  });

  it('is focusable by a form that reveals it, without joining the tab order', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.sectionId = 'section-details';
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const section = host.querySelector<HTMLElement>('#section-details')!;

    expect(section.tagName).toBe('SECTION');
    expect(section.getAttribute('tabindex')).toBe('-1');

    section.focus();
    expect(document.activeElement).toBe(section);
  });

  it('carries no id at all until a form asks for one', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('section')!.hasAttribute('id')).toBeFalse();
  });

  it('gives two sections on one page different heading ids', async () => {
    await TestBed.configureTestingModule({ imports: [TwoSectionHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TwoSectionHostComponent);
    fixture.detectChanges();

    const sections = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('section'));
    const ids = sections.map((section) => section.getAttribute('aria-labelledby'));

    expect(ids.length).toBe(2);
    expect(ids[0]).not.toBe(ids[1]);
    expect(new Set(ids).size).toBe(2);
  });

  it('projects its fields into the section it names', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const section = (fixture.nativeElement as HTMLElement).querySelector('section')!;
    expect(section.querySelector('#title-control')).not.toBeNull();
  });
});
