import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EMPTY } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { ContentPipelineImagesState } from '../../models/content-pipeline.models';
import { RequestGeneratedImagesRequest } from '../../models/generated-image.models';
import { GeneratedImageRequestOutcome, GeneratedImageService } from '../../services/generated-image.service';
import {
  GeneratedImageRunComponent,
  GeneratedImageRunWording,
  PIPELINE_IMAGE_RUN_WORDING,
} from './generated-image-run.component';

// What a run does — asking, watching, choosing, declining, tidying up, and staying inside one workspace — is
// covered through its first caller in `content-pipeline-images-step.component.spec.ts`, which drives this
// component through a one-line wrapper. This file covers only what that caller never varies: the inputs a
// second caller sets.

const ELSEWHERE: GeneratedImageRunWording = {
  makeIntro: 'The prompt above, as it will be sent.',
  noPrompt: 'Write a prompt above first.',
  tooLongRemedy: 'Shorten it above.',
  countRemedy: 'Change that in the brief.',
  forbidden: 'Ask an Owner to make them.',
  keepNote: 'A mark, not a filing.',
};

@Component({
  imports: [GeneratedImageRunComponent],
  template: `<cp-generated-image-run
    workspaceSlug="cozy-fall"
    [promptText]="promptText()"
    [avoidText]="avoidText()"
    [variantCount]="variantCount()"
    [state]="state()"
    [wording]="wording()"
    [sectionIdPrefix]="prefix()"
    (changed)="state.set($event)"
  />`,
})
class HostComponent {
  readonly promptText = signal('A tight crop.');
  readonly avoidText = signal<string | null>(null);
  readonly variantCount = signal(2);
  readonly state = signal<ContentPipelineImagesState>({ operationId: null, keepers: [] });
  readonly wording = signal<GeneratedImageRunWording>(ELSEWHERE);
  readonly prefix = signal('cp-studio-images');
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let requests: RequestGeneratedImagesRequest[];
let requestOutcome: GeneratedImageRequestOutcome;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function makeButton(): HTMLButtonElement {
  return Array.from(el.querySelectorAll('button')).find(
    (button) => (button.textContent ?? '').trim() === 'Make the pictures',
  ) as HTMLButtonElement;
}

describe('GeneratedImageRunComponent', () => {
  beforeEach(async () => {
    requests = [];
    requestOutcome = { status: 'unavailable' };

    await TestBed.configureTestingModule({
      providers: [
        {
          provide: GeneratedImageService,
          useValue: {
            request: (_slug: string, request: RequestGeneratedImagesRequest) => {
              requests.push(request);

              return Promise.resolve(requestOutcome);
            },
            watch: () => EMPTY,
            preview: () => EMPTY,
            reject: () => Promise.resolve({ status: 'declined' as const }),
            downloadUrl: () => null,
          },
        },
        { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    await settle();
  });

  it("says its caller's sentences rather than the pipeline's", async () => {
    expect(el.textContent).toContain(ELSEWHERE.makeIntro);
    expect(el.textContent).toContain('Asking for 2 pictures. Change that in the brief.');
    expect(el.textContent).not.toContain(PIPELINE_IMAGE_RUN_WORDING.countRemedy);

    host.promptText.set('   ');
    await settle();

    expect(el.textContent).toContain(ELSEWHERE.noPrompt);
    expect(makeButton().disabled).toBeTrue();
  });

  it('says where to shorten a prompt that is too long to send, and will not send it', async () => {
    host.promptText.set('x'.repeat(4001));
    await settle();

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('4001 of 4000 characters. Shorten it above.');
    expect(makeButton().disabled).toBeTrue();
  });

  it("tells a refused member so in its caller's words", async () => {
    requestOutcome = { status: 'forbidden' };

    makeButton().click();
    await settle();

    expect(el.querySelector('[role="alert"]')?.textContent).toContain(ELSEWHERE.forbidden);
  });

  it('names its sections with the prefix it was given, so a caller can navigate to them', () => {
    expect(el.querySelector('#cp-studio-images-make')).not.toBeNull();
    expect(el.querySelector('#cp-pipeline-images-make')).toBeNull();
  });

  it('sends the prompt trimmed, the avoid list it was handed, and a count held within what a run allows', async () => {
    host.promptText.set('  A tight crop.  ');
    host.avoidText.set('clutter, harsh light');
    host.variantCount.set(9);
    await settle();

    makeButton().click();
    await settle();

    expect(requests).toEqual([{ promptText: 'A tight crop.', avoidText: 'clutter, harsh light', variantCount: 4 }]);
  });

  it('defaults to the pipeline wording, so its first caller passes none', () => {
    host.wording.set(PIPELINE_IMAGE_RUN_WORDING);
    fixture.detectChanges();

    expect(el.textContent).toContain('Change that on the first step.');
  });
});
