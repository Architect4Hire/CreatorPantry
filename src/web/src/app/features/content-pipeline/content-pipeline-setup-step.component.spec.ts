import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ContentChannel } from '../../models/brand-profile.models';
import {
  ContentPipelineConfig,
  emptyContentPipelineConfig,
} from '../../models/content-pipeline.models';
import { BrandProfileService, ContentChannelsOutcome } from '../../services/brand-profile.service';
import { ContentPipelineSetupStepComponent } from './content-pipeline-setup-step.component';

const CHANNELS: readonly ContentChannel[] = [
  { key: 'blog', displayName: 'Blog', isActive: true },
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'vine', displayName: 'Vine', isActive: false },
];

@Component({
  imports: [ContentPipelineSetupStepComponent],
  template: `<cp-content-pipeline-setup-step [config]="config()" (changed)="apply($event)" />`,
})
class HostComponent {
  readonly config = signal<ContentPipelineConfig>(emptyContentPipelineConfig());
  readonly emitted: ContentPipelineConfig[] = [];

  apply(next: ContentPipelineConfig): void {
    this.emitted.push(next);
    this.config.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let channels: ContentChannelsOutcome;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(config: ContentPipelineConfig = emptyContentPipelineConfig()): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.config.set(config);
  el = fixture.nativeElement;
  await settle();
}

const latest = (): ContentPipelineConfig => host.emitted[host.emitted.length - 1];
const byId = <T extends HTMLElement>(id: string): T => el.querySelector<T>('#' + id)!;

function buttonWith(text: string): HTMLButtonElement {
  return Array.from(el.querySelectorAll('button')).find((button) =>
    (button.textContent ?? '').trim().startsWith(text),
  ) as HTMLButtonElement;
}

async function type(id: string, value: string): Promise<void> {
  const control = byId<HTMLInputElement | HTMLTextAreaElement>(id);
  control.value = value;
  control.dispatchEvent(new Event('input'));
  await settle();
}

describe('ContentPipelineSetupStepComponent', () => {
  beforeEach(() => {
    channels = { status: 'found', channels: CHANNELS };
    TestBed.configureTestingModule({
      providers: [
        { provide: BrandProfileService, useValue: { listContentChannels: () => Promise.resolve(channels) } },
      ],
    });
  });

  it('offers the active channels and no particular channel at all', async () => {
    await mount();

    const options = Array.from(byId<HTMLSelectElement>('cp-pipeline-channel').options).map((option) => option.value);
    expect(options).toEqual(['', 'blog', 'instagram']);
  });

  it('still shows a retired channel that is already chosen, so a creator is not told it does not exist', async () => {
    await mount({ ...emptyContentPipelineConfig(), channelKey: 'vine' });

    const select = byId<HTMLSelectElement>('cp-pipeline-channel');
    const options = Array.from(select.options).map((option) => option.textContent?.trim());
    expect(options).toContain('Vine (no longer offered)');
    expect(select.value).toBe('vine');
  });

  it('leaves the channel field usable-but-empty and says so when the list cannot be read', async () => {
    channels = { status: 'unavailable' };
    await mount();

    expect(byId<HTMLSelectElement>('cp-pipeline-channel').disabled).toBeTrue();
    expect(el.textContent).toContain('list of channels could not be loaded');
    // The rest of the step still works: a creator who cannot reach the vocabulary can still describe a picture.
    await type('cp-pipeline-concept', 'A tight crop of the first slice.');
    expect(latest().concept).toBe('A tight crop of the first slice.');
  });

  it('defaults to two pictures and reports a different count as a number', async () => {
    await mount();

    const two = el.querySelector<HTMLInputElement>('input[value="2"]')!;
    expect(two.checked).toBeTrue();

    el.querySelector<HTMLInputElement>('input[value="4"]')!.click();
    await settle();

    expect(latest().variantCount).toBe(4);
  });

  it('offers a day and no particular day, and reports the day by name', async () => {
    await mount();

    const friday = Array.from(el.querySelectorAll<HTMLInputElement>('input[type="radio"]')).find(
      (input) => input.value === 'Friday',
    )!;
    friday.click();
    await settle();

    expect(latest().day).toBe('Friday');
  });

  it('keeps the concept exactly as it was typed and caps it at the length the server accepts', async () => {
    await mount();

    expect(byId<HTMLTextAreaElement>('cp-pipeline-concept').maxLength).toBe(1000);

    await type('cp-pipeline-concept', '  Two hands lifting the lid.  ');
    expect(latest().concept).toBe('  Two hands lifting the lid.  ');
  });

  it('adds, edits and removes a scene element, dropping a row the creator emptied', async () => {
    await mount();
    expect(el.textContent).toContain('Nothing yet');

    buttonWith('Add a scene element').click();
    await settle();

    const row = el.querySelector<HTMLInputElement>('input[id^="cp-pipeline-scene-"]')!;
    row.value = 'marble slab';
    row.dispatchEvent(new Event('input'));
    await settle();
    expect(latest().scene).toEqual(['marble slab']);

    row.value = '   ';
    row.dispatchEvent(new Event('input'));
    await settle();
    expect(latest().scene).toEqual([]);

    buttonWith('Remove').click();
    await settle();
    expect(el.querySelector('input[id^="cp-pipeline-scene-"]')).toBeNull();
  });

  it('stops adding scene elements at the cap, and says why', async () => {
    await mount({ ...emptyContentPipelineConfig(), scene: Array.from({ length: 10 }, (_, i) => `p${i}`) });

    expect(buttonWith('Add a scene element').disabled).toBeTrue();
    expect(el.textContent).toContain('as many scene elements as one picture can carry');
  });

  it('caps one scene element at the length the server accepts', async () => {
    await mount({ ...emptyContentPipelineConfig(), scene: ['marble slab'] });

    expect(el.querySelector<HTMLInputElement>('input[id^="cp-pipeline-scene-"]')!.maxLength).toBe(300);
  });

  it('names every Remove so a screen reader hears which row it belongs to', async () => {
    await mount({ ...emptyContentPipelineConfig(), scene: ['marble slab'], style: ['soft window light'] });

    const labels = Array.from(el.querySelectorAll('button[aria-label]')).map((button) =>
      button.getAttribute('aria-label'),
    );
    expect(labels).toContain('Remove scene element 1');
    expect(labels).toContain('Remove style direction 1');
  });

  it('clears the rows on screen when the host replaces the draft, so a new run shows no old scene', async () => {
    await mount({ ...emptyContentPipelineConfig(), scene: ['marble slab'], style: ['soft window light'] });
    expect(el.querySelector<HTMLInputElement>('input[id^="cp-pipeline-scene-"]')!.value).toBe('marble slab');

    host.config.set(emptyContentPipelineConfig());
    await settle();

    expect(el.querySelector('input[id^="cp-pipeline-scene-"]')).toBeNull();
    expect(el.querySelector('input[id^="cp-pipeline-style-"]')).toBeNull();
  });

  it('keeps a row the creator has just added and not yet filled in', async () => {
    await mount();

    buttonWith('Add a scene element').click();
    await settle();

    expect(el.querySelector('input[id^="cp-pipeline-scene-"]')).not.toBeNull();
    expect(latest()).toBeUndefined();
  });

  it('marks no field optional, because the step says it once in its own legend', async () => {
    await mount();

    expect(el.querySelectorAll('[aria-required="true"]').length).toBe(0);
    expect(el.textContent).not.toContain('(optional)');
  });
});
