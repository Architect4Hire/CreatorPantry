import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BRAND_SETUP_STEPS, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSetupGoalsStepComponent } from './brand-setup-goals-step.component';
import { decodeGoals, websiteProblem } from './brand-setup-goals';

@Component({
  imports: [BrandSetupGoalsStepComponent],
  template: `<cp-brand-setup-goals-step [step]="step" [draft]="draft()" (reported)="reports.push($event)" />`,
})
class HostComponent {
  readonly step = BRAND_SETUP_STEPS[0];
  readonly draft = signal<Record<string, unknown> | null>(null);
  readonly reports: BrandSetupStepReport[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let el: HTMLElement;
let host: HostComponent;

async function mount(draft: Record<string, unknown> | null = null): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(draft);
  el = fixture.nativeElement;
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

const last = (): BrandSetupStepReport => host.reports[host.reports.length - 1];
const input = (id: string): HTMLInputElement => el.querySelector<HTMLInputElement>('#' + id)!;

async function click(id: string): Promise<void> {
  input(id).click();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('BrandSetupGoalsStepComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('starts with the audience and channel defaults and nothing to save', async () => {
    await mount();
    expect(input('cp-goals-audience-home-cooks').checked).toBeTrue();
    expect(input('cp-goals-channel-blog').checked).toBeTrue();
    expect(input('cp-goals-channel-instagram').checked).toBeTrue();
    expect(input('cp-goals-channel-tiktok').checked).toBeFalse();
    expect(last().canContinue).toBeFalse();
    expect(last().isDirty).toBeFalse();
    expect(last().draft).toBeNull();
  });

  it('can continue once a purpose is picked, and hands over its answers', async () => {
    await mount();
    await click('cp-goals-purpose-blog');
    expect(last().canContinue).toBeTrue();
    expect(last().isDirty).toBeTrue();
    expect(last().draft).toEqual(jasmine.objectContaining({ purposes: ['blog'], audience: 'home-cooks', channels: ['blog', 'instagram'] }));
  });

  it('cannot continue with no place to share', async () => {
    await mount();
    await click('cp-goals-purpose-blog');
    await click('cp-goals-channel-blog');
    await click('cp-goals-channel-instagram');
    expect(last().canContinue).toBeFalse();
    expect(el.textContent).toContain('Pick at least one');
  });

  it('shows the optional note only for "other" choices', async () => {
    await mount();
    expect(el.querySelector('#cp-goals-purpose-note')).toBeNull();
    await click('cp-goals-purpose-other');
    expect(el.querySelector('#cp-goals-purpose-note')).not.toBeNull();
    await click('cp-goals-audience-other');
    expect(el.querySelector('#cp-goals-audience-note')).not.toBeNull();
  });

  it('restores saved answers and keeps them as the draft', async () => {
    await mount({ purposes: ['social'], audience: 'beginners', channels: ['tiktok'], website: 'https://example.com' });
    expect(input('cp-goals-purpose-social').checked).toBeTrue();
    expect(input('cp-goals-audience-beginners').checked).toBeTrue();
    expect(input('cp-goals-channel-tiktok').checked).toBeTrue();
    expect(input('cp-goals-channel-blog').checked).toBeFalse();
    expect(el.querySelector('details.more')!.hasAttribute('open')).toBeTrue();
    expect(last().canContinue).toBeTrue();
    expect(last().isDirty).toBeFalse();
  });

  it('ignores a malformed saved draft', () => {
    const a = decodeGoals({ purposes: 'blog', audience: 42, channels: ['nope'], website: {} });
    expect(a.purposes).toEqual([]);
    expect(a.audience).toBe('home-cooks');
    expect(a.channels).toEqual([]);
    expect(a.website).toBe('');
  });

  it('blocks Continue and explains a website address that is not a web address', async () => {
    await mount();
    await click('cp-goals-purpose-blog');
    const website = input('cp-goals-website');
    website.value = 'not a url';
    website.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(last().canContinue).toBeFalse();
    expect(website.getAttribute('aria-invalid')).toBe('true');
    expect(el.textContent).toContain('Enter a web address');
    expect(websiteProblem('https://example.com')).toBe('');
    expect(websiteProblem('')).toBe('');
  });

  it('is accessible: every question is a labelled group and every control has a name', async () => {
    await mount();
    const groups = Array.from(el.querySelectorAll('[role="group"], [role="radiogroup"]'));
    expect(groups.length).toBe(3);
    for (const g of groups) expect(g.getAttribute('aria-label')).toBeTruthy();
    expect(el.querySelectorAll('cp-form-section').length).toBe(4);
    for (const control of Array.from(el.querySelectorAll<HTMLInputElement>('input'))) {
      const named = control.labels?.length || control.getAttribute('aria-label');
      expect(named).withContext(control.id).toBeTruthy();
    }
    expect(el.querySelectorAll('button').length).toBe(0);
  });

  it('uses no model or prompt vocabulary', async () => {
    await mount();
    expect(el.textContent!.toLowerCase()).not.toMatch(/\b(prompt|model|token|ai|upload)\b/);
  });
});
