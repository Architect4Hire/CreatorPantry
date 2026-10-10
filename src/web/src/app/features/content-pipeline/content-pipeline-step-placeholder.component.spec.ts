import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ContentPipelineStepDefinition } from '../../models/content-pipeline.models';
import { ContentPipelineStepPlaceholderComponent } from './content-pipeline-step-placeholder.component';

const STEP: ContentPipelineStepDefinition = {
  slug: 'later',
  label: 'Something later',
  help: 'What this step will be for.',
  legend: '',
  comingSoon: 'This one is coming soon.',
};

@Component({
  imports: [ContentPipelineStepPlaceholderComponent],
  template: `<cp-content-pipeline-step-placeholder [step]="step()" />`,
})
class HostComponent {
  readonly step = signal(STEP);
}

let fixture: ComponentFixture<HostComponent>;
let el: HTMLElement;

/**
 * The body of a step that has not shipped.
 *
 * Covered on its own since AF.6.5, which built the last step that used it: the shell's own spec used to be
 * where this was proved, and it had to stop being. The component stays because the step list declares the
 * whole journey — the next step added arrives unbuilt, and this is what it will render.
 */
describe('ContentPipelineStepPlaceholderComponent', () => {
  beforeEach(async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({}).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    el = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('says the step is coming, and what it will be for', () => {
    expect(el.textContent).toContain('This one is coming soon.');
    expect(el.textContent).toContain('What this step will be for.');
  });

  /** No action at all — not a disabled one — so there is nothing to mistake for something the product does. */
  it('offers no control of any kind', () => {
    expect(el.querySelectorAll('button').length).toBe(0);
    expect(el.querySelectorAll('input, textarea, select, a').length).toBe(0);
  });

  it('says it as a note rather than as something gone wrong', () => {
    expect(el.querySelector('cp-notice')?.getAttribute('role')).toBe('note');
  });
});
