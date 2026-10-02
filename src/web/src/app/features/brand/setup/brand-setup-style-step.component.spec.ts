import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BRAND_SETUP_STEPS, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSetupStyleStepComponent } from './brand-setup-style-step.component';
import { STYLE_QUESTIONS, decodeStyle } from './brand-setup-style';

@Component({
  imports: [BrandSetupStyleStepComponent],
  template: `<cp-brand-setup-style-step [step]="step" [draft]="draft()" (reported)="reports.push($event)" />`,
})
class HostComponent {
  readonly step = BRAND_SETUP_STEPS[1];
  readonly draft = signal<Record<string, unknown> | null>(null);
  readonly reports: BrandSetupStepReport[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let el: HTMLElement;
let host: HostComponent;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(draft: Record<string, unknown> | null = null): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(draft);
  el = fixture.nativeElement;
  await settle();
}

const last = (): BrandSetupStepReport => host.reports[host.reports.length - 1];
const input = (id: string): HTMLInputElement => el.querySelector<HTMLInputElement>('#' + id)!;

async function click(id: string): Promise<void> {
  input(id).click();
  await settle();
}

describe('BrandSetupStyleStepComponent', () => {
  it('asks the questions in the approved order, with nothing preselected', async () => {
    await mount();
    const headings = Array.from(el.querySelectorAll('cp-form-section h2')).map((h) => h.textContent?.trim());
    expect(headings).toEqual(STYLE_QUESTIONS.map((q) => q.heading));
    // Only each question's "Not sure yet" is selected; no real choice is.
    expect(el.querySelectorAll('input:checked').length).toBe(STYLE_QUESTIONS.filter((q) => !q.multiple).length);
    expect(Array.from(el.querySelectorAll<HTMLInputElement>('input:checked')).every((i) => i.id.endsWith('-unsure'))).toBeTrue();
  });

  it('always lets the creator continue, and saves nothing while blank', async () => {
    await mount();
    expect(last().canContinue).toBeTrue();
    expect(last().isDirty).toBeFalse();
    expect(last().draft).toBeNull();
  });

  it('shows a sample sentence beside the choices that have one', async () => {
    await mount();
    expect(el.textContent).toContain('Okay, this lasagna is going to be your new favorite.');
    expect(el.textContent).toContain('Brown it. Drain it. Layer it.');
  });

  it('hands over a pick and lets "Not sure yet" clear it', async () => {
    await mount();
    await click('cp-style-voice-warm-friend');
    expect(last().isDirty).toBeTrue();
    expect(last().draft).toEqual(jasmine.objectContaining({ choices: jasmine.objectContaining({ voice: ['warm-friend'] }) }));
    await click('cp-style-voice-unsure');
    expect((last().draft as { choices: Record<string, string[]> }).choices['voice']).toEqual([]);
    expect(input('cp-style-voice-warm-friend').checked).toBeFalse();
  });

  it('keeps one answer per single-choice question', async () => {
    await mount();
    await click('cp-style-formality-casual');
    await click('cp-style-formality-polished');
    expect(input('cp-style-formality-casual').checked).toBeFalse();
    expect(input('cp-style-formality-polished').checked).toBeTrue();
  });

  it('caps the feel question at three and disables the rest until one is unticked', async () => {
    await mount();
    for (const k of ['cozy', 'upbeat', 'calm']) await click('cp-style-tones-' + k);
    expect(input('cp-style-tones-confident').disabled).toBeTrue();
    expect(el.textContent).toContain("That's 3.");
    await click('cp-style-tones-calm');
    expect(input('cp-style-tones-confident').disabled).toBeFalse();
  });

  it('saves free text, trimmed to its limit', async () => {
    await mount();
    const note = input('cp-style-cta-note');
    note.value = 'x'.repeat(250);
    note.dispatchEvent(new Event('input'));
    await settle();
    expect((last().draft as { notes: Record<string, string> }).notes['cta'].length).toBe(200);
  });

  it('restores saved answers and treats unknown ones as blank', async () => {
    await mount({ choices: { voice: ['calm-teacher'], humor: ['bogus'] }, notes: { words: 'plain' } });
    expect(input('cp-style-voice-calm-teacher').checked).toBeTrue();
    expect(input('cp-style-humor-unsure').checked).toBeTrue();
    expect(input('cp-style-words-note').value).toBe('plain');
    expect(last().isDirty).toBeFalse();
    const bad = decodeStyle({ choices: 'x', notes: [] });
    expect(bad.choices.voice).toEqual([]);
  });

  it('is accessible: grouped, named, keyboard-native, and free of jargon', async () => {
    await mount();
    const groups = Array.from(el.querySelectorAll('[role="group"], [role="radiogroup"]'));
    expect(groups.length).toBe(STYLE_QUESTIONS.length);
    for (const g of groups) expect(g.getAttribute('aria-label')).toBeTruthy();
    for (const control of Array.from(el.querySelectorAll<HTMLInputElement>('input'))) {
      expect(control.labels?.length || control.getAttribute('aria-label')).withContext(control.id).toBeTruthy();
      expect(control.getAttribute('tabindex')).not.toBe('-1');
    }
    expect(el.querySelectorAll('button').length).toBe(0);
    expect(el.textContent!.toLowerCase()).not.toMatch(/\b(tenor|register|prompt|model|token|ai)\b/);
  });
});
