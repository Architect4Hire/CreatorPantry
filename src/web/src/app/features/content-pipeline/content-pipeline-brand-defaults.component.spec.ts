import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { BrandVisualGuide } from '../../models/brand-visual-guide.models';
import { ContentPipelineConfig, emptyContentPipelineConfig } from '../../models/content-pipeline.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandVisualGuideOutcome, BrandVisualGuideService } from '../../services/brand-visual-guide.service';
import {
  ContentPipelineBrandDefaultsComponent,
  ContentPipelineBrandStatus,
} from './content-pipeline-brand-defaults.component';

const GUIDE: BrandVisualGuide = {
  activeGuide: {
    guideId: 'g1',
    name: 'House style',
    versionNumber: 3,
    approvedAt: '2026-09-20T10:00:00Z',
    isStale: false,
    staleSourceCount: 0,
    appliedSections: ['PhotographyDirection'],
  },
  hasVisualGuidance: true,
  styleLines: [{ label: 'Photography direction', summary: 'Soft window light, linen, nothing glossy.' }],
  negativeGuidance: 'No studio flash.',
  references: [],
  referencesTruncated: false,
  referencesAvailable: true,
};

@Component({
  imports: [ContentPipelineBrandDefaultsComponent],
  template: `<cp-content-pipeline-brand-defaults
    [workspaceSlug]="slug()"
    [config]="config()"
    [brandStatus]="status()"
    [brandChannelKeys]="channelKeys()"
    [brandPictureCount]="pictureCount()"
    (changed)="apply($event)"
  />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly config = signal<ContentPipelineConfig>(emptyContentPipelineConfig());
  readonly status = signal<ContentPipelineBrandStatus>('ready');
  readonly channelKeys = signal<readonly string[]>(['instagram', 'blog']);
  readonly pictureCount = signal<number | null>(3);
  readonly emitted: ContentPipelineConfig[] = [];

  apply(next: ContentPipelineConfig): void {
    this.emitted.push(next);
    this.config.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let guide: BrandVisualGuideOutcome;
let guideReads: string[];

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(arrange: (host: HostComponent) => void = () => undefined): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  arrange(host);
  await settle();
}

/** The panel itself: the drawer moves its content to `<body>` while open, so that is read from the document. */
const panel = (): HTMLElement => fixture.nativeElement.querySelector('cp-form-section');
const text = (): string => (panel().textContent ?? '').replace(/\s+/g, ' ');
const fact = (label: string): string => {
  const term = Array.from(panel().querySelectorAll('dt')).find((each) => each.textContent?.trim() === label);

  // `innerText`, which reads a value and the words beside it as the separate things they are on screen.
  return (term?.parentElement?.querySelector<HTMLElement>('dd')?.innerText ?? '').replace(/\s+/g, ' ').trim();
};

function buttonWith(label: string, within: ParentNode = document): HTMLButtonElement {
  return Array.from(within.querySelectorAll('button')).find(
    (each) => each.textContent?.trim() === label,
  ) as HTMLButtonElement;
}

describe('ContentPipelineBrandDefaultsComponent', () => {
  beforeEach(() => {
    guide = { status: 'found', guide: GUIDE };
    guideReads = [];

    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: BrandProfileService,
          useValue: {
            listContentChannels: () =>
              Promise.resolve({
                status: 'found',
                channels: [
                  { key: 'instagram', displayName: 'Instagram', isActive: true },
                  { key: 'blog', displayName: 'Blog', isActive: true },
                ],
              }),
          },
        },
        {
          provide: BrandVisualGuideService,
          useValue: {
            get: (slug: string) => {
              guideReads.push(slug);
              return Promise.resolve(guide);
            },
          },
        },
      ],
    });
  });

  it('shows where the posts go, how many pictures to try and the look, each from the brand', async () => {
    await mount();

    expect(fact('Posts are written for')).toBe('Instagram Blog');
    expect(fact('Pictures to try')).toBe('3 pictures From your brand');
    expect(fact('The look')).toContain('House style');
    expect(fact('The look')).toContain('Version 3 of your style guide');
    expect(fact('The look')).toContain('Soft window light, linen, nothing glossy.');
    expect(fact('The look')).toContain('No studio flash.');
    expect(text()).not.toContain('Added for this run');
  });

  it('says the count is the product’s own when the brand states none', async () => {
    await mount((each) => each.pictureCount.set(null));

    expect(fact('Pictures to try')).toBe('2 pictures CreatorPantry’s default');
  });

  it('says a count, a channel and added lines are this run’s, beside what the brand says', async () => {
    await mount((each) =>
      each.config.set({
        ...emptyContentPipelineConfig(),
        channelKey: 'blog',
        variantCount: 1,
        variantCountChosen: true,
        scene: ['A marble board'],
        style: ['Moody'],
      }),
    );

    expect(fact('Pictures to try')).toBe('1 picture For this run');
    expect(fact('This picture is for')).toBe('Blog For this run');
    expect(fact('Added for this run')).toBe('A marble board Moody');
    // The brand's own answers are still shown: a run adds to them, it does not replace them.
    expect(fact('Posts are written for')).toBe('Instagram Blog');
  });

  it('carries on without a brand, and says the run is using the product’s defaults', async () => {
    await mount((each) => {
      each.status.set('none');
      each.channelKeys.set([]);
      each.pictureCount.set(null);
    });

    expect(text()).toContain('no brand settings yet');
    expect(fact('Posts are written for')).toContain('No default channels yet');
    expect(fact('Pictures to try')).toBe('2 pictures CreatorPantry’s default');

    host.status.set('unavailable');
    await settle();

    expect(text()).toContain('could not be loaded');
    expect(buttonWith('Adjust for this run', panel())).toBeTruthy();
  });

  it('says there is no active style guide rather than inventing a look', async () => {
    guide = {
      status: 'found',
      guide: { ...GUIDE, activeGuide: null, hasVisualGuidance: false, styleLines: [], negativeGuidance: null },
    };
    await mount();

    expect(fact('The look')).toBe('No style guide is active, so the looks are planned without one.');
    expect(panel().querySelector('a[href$="/brand/style-guide/g1"]')).toBeNull();
  });

  it('opens a drawer that changes this run only, keeping each change as it is made', async () => {
    await mount();
    const adjust = buttonWith('Adjust for this run', panel());

    adjust.click();
    await settle();

    const drawer = document.querySelector('[role="dialog"]') as HTMLElement;
    expect(drawer.textContent).toContain('Adjust for this run');
    expect(document.activeElement === adjust || drawer.contains(document.activeElement)).toBeTrue();
    // The brand's count is the answer already given, and only a choice here replaces it.
    expect((document.querySelector('input[type="radio"]:checked') as HTMLInputElement).value).toBe('3');

    const four = Array.from(document.querySelectorAll<HTMLInputElement>('input[type="radio"]')).find(
      (each) => each.value === '4',
    )!;
    four.click();
    await settle();

    expect(host.emitted[host.emitted.length - 1]).toEqual(
      jasmine.objectContaining({ variantCount: 4, variantCountChosen: true }),
    );
    expect(fact('Pictures to try')).toBe('4 pictures For this run');

    buttonWith('Done').click();
    await settle();

    expect(document.querySelector('cp-content-pipeline-setup-step')).toBeNull();
  });

  it('reads the look again for another workspace, and shows nothing of the first while it does', async () => {
    await mount();
    expect(guideReads).toEqual(['cozy-fall']);

    guide = {
      status: 'found',
      guide: { ...GUIDE, activeGuide: { ...GUIDE.activeGuide!, guideId: 'g2', name: 'Their style' }, styleLines: [] },
    };
    host.slug.set('other-kitchen');
    fixture.detectChanges();

    expect(fact('The look')).not.toContain('House style');

    await settle();

    expect(guideReads).toEqual(['cozy-fall', 'other-kitchen']);
    expect(fact('The look')).toContain('Their style');
    expect(fact('The look')).not.toContain('Soft window light');
  });
});
