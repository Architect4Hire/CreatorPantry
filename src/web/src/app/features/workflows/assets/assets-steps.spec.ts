import { Component, Type, input, output, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, of } from 'rxjs';

import { CreativeContext, CreativeContextReference } from '../../../models/creative-context.models';
import { DamAssetDetail, DamAssetSummary, DamAssetVersion, DamCreatedAsset } from '../../../models/dam-asset.models';
import { WorkflowResourcePointer, WorkflowRun, decodeWorkflowRun } from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { BrandProfileService } from '../../../services/brand-profile.service';
import { CreativeContextService } from '../../../services/creative-context.service';
import { DamAssetService } from '../../../services/dam-asset.service';
import { GeneratedImageService } from '../../../services/generated-image.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { SaveToLibraryDialogComponent } from '../../../shared/save-to-library/save-to-library-dialog.component';
import { DamAssetEditComponent } from '../../dam/dam-asset-edit.component';
import { DamAssetLogUseComponent } from '../../dam/dam-asset-log-use.component';
import { DamAssetPickerComponent } from '../../dam/dam-asset-picker.component';
import { DamAssetUploadComponent } from '../../dam/dam-asset-upload.component';
import { DamAssetUsageHistoryComponent } from '../../dam/dam-asset-usage-history.component';
import { DamAssetVersionUploadComponent } from '../../dam/dam-asset-version-upload.component';
import { DamLinkedAssetComponent } from '../../dam/dam-linked-asset.component';
import { AssetsAddStepComponent } from './assets-add-step.component';
import { AssetsDetailStepComponent } from './assets-detail-step.component';
import { AssetsUseStepComponent } from './assets-use-step.component';

const SLUG = 'sams-kitchen';

// ---- The library's own parts, stood in for: each says what the step handed it, and is driven by the spec. ----

@Component({ selector: 'cp-dam-asset-upload', template: `<p class="stub-upload">{{ open() }}</p>` })
class StubUpload {
  readonly open = input(false);
  readonly workspaceSlug = input<string>();
  readonly uploaded = output<DamCreatedAsset>();
  readonly cancelled = output<void>();
  readonly closed = output<void>();
}

@Component({ selector: 'cp-dam-asset-picker', template: `<p class="stub-picker"></p>` })
class StubPicker {
  readonly workspaceSlug = input<string>();
  readonly active = input(true);
  readonly chosen = output<DamAssetSummary>();
}

@Component({ selector: 'cp-dam-asset-version-upload', template: `<p class="stub-version">{{ open() }}</p>` })
class StubVersionUpload {
  readonly open = input(false);
  readonly workspaceSlug = input<string>();
  readonly assetId = input<string>();
  readonly currentVersionNumber = input(1);
  readonly added = output<DamAssetVersion>();
  readonly cancelled = output<void>();
  readonly closed = output<void>();
}

@Component({ selector: 'cp-dam-linked-asset', template: `<p class="stub-linked">{{ assetId() }}</p>` })
class StubLinked {
  readonly workspaceSlug = input<string>();
  readonly assetId = input<string>();
}

@Component({
  selector: 'cp-save-to-library-dialog',
  template: `<p class="stub-save">{{ generatedImageId() }} {{ creativeContextId() }}</p>`,
})
class StubSave {
  readonly open = input(false);
  readonly workspaceSlug = input<string>();
  readonly generatedImageId = input<string>();
  readonly defaultTitle = input('');
  readonly creativeContextId = input<string | null>(null);
  readonly saved = output<DamCreatedAsset>();
  readonly imageGone = output<void>();
  readonly closed = output<void>();
}

@Component({
  selector: 'cp-dam-asset-edit',
  template: `<p class="stub-edit">{{ sections() }}</p>`,
  providers: [{ provide: DamAssetEditComponent, useExisting: StubEdit }],
})
class StubEdit {
  readonly inline = input(false);
  readonly sections = input<string>('all');
  readonly workspaceSlug = input<string>();
  readonly asset = input<DamAssetDetail>();
  readonly channels = input<unknown>();
  readonly saved = output<DamAssetDetail>();
  readonly refreshed = output<DamAssetDetail>();
  readonly dirty = signal(false);
  readonly saving = signal(false);
  saveAnswer = true;
  saves = 0;

  saveForStep(): Promise<boolean> {
    this.saves += 1;
    return Promise.resolve(this.saveAnswer);
  }
}

@Component({
  selector: 'cp-dam-asset-log-use',
  template: `<p class="stub-use">{{ assetId() }}</p>`,
  providers: [{ provide: DamAssetLogUseComponent, useExisting: StubLogUse }],
})
class StubLogUse {
  readonly inline = input(false);
  readonly workspaceSlug = input<string>();
  readonly assetId = input<string>();
  readonly logged = output<unknown>();
  readonly assetGone = output<void>();
  readonly dirty = signal(false);
  readonly logging = signal(false);
  logs = 0;

  logForStep(): Promise<boolean> {
    this.logs += 1;
    return Promise.resolve(true);
  }
}

@Component({ selector: 'cp-dam-asset-usage-history', template: `<p class="stub-history">{{ reloadToken() }}</p>` })
class StubHistory {
  readonly workspaceSlug = input<string>();
  readonly assetId = input<string>();
  readonly reloadToken = input(0);
}

function asset(overrides: Partial<DamAssetDetail> = {}): DamAssetDetail {
  return {
    id: 'asset-1',
    title: 'Galette, first slice',
    description: null,
    altText: null,
    kind: 'AiGenerated',
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    rightsHolder: null,
    attributionText: null,
    tags: [],
    currentVersion: { versionNumber: 2 } as DamAssetVersion,
    versions: [{ versionNumber: 2 } as DamAssetVersion, { versionNumber: 1 } as DamAssetVersion],
    utilizationCount: 0,
    recipeLinks: [{ recipeId: 'recipe-1', title: 'Plum galette', role: 'Hero', caption: null }],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [],
    sourceGeneratedImageId: 'img-1',
    createdAt: '2026-10-15T10:00:00Z',
    updatedAt: '2026-10-15T10:00:00Z',
    concurrencyToken: 'token-1',
    ...overrides,
  };
}

function keeper(generatedImageId: string, overrides: Partial<CreativeContextReference> = {}): CreativeContextReference {
  return {
    id: `ref-${generatedImageId}`,
    kind: 'GeneratedImage',
    purpose: 'Keeper',
    sortOrder: 0,
    recipeId: null,
    recipeVersionId: null,
    conceptRequestId: null,
    conceptId: null,
    mediaAssetId: null,
    mediaAssetVersionNumber: null,
    generatedImageId,
    promptRecordId: null,
    addedAt: '2026-10-15T10:00:00Z',
    ...overrides,
  };
}

function context(
  id: string,
  workingTitle: string | null,
  references: readonly CreativeContextReference[],
): CreativeContext {
  return {
    id,
    workingTitle,
    pictureBrief: null,
    briefSource: null,
    workingBrief: null,
    channelKeys: [],
    day: null,
    weeklyThemeKey: null,
    references,
    createdAt: '2026-10-15T09:00:00Z',
    updatedAt: '2026-10-15T10:00:00Z',
    archivedAt: null,
    concurrencyToken: 't',
  };
}

function organizeRun(links: readonly { kind: string; resourceId: string; stepKey: string | null }[]): WorkflowRun {
  return decodeWorkflowRun(wireRun({ id: 'run-1', definitionKey: 'assets.organize', links }))!;
}

const withPicture = () => organizeRun([{ kind: 'MediaAsset', resourceId: 'asset-1', stepKey: null }]);

let port: WorkflowStepPort;
let keepDraft: jasmine.Spy<(pointer: WorkflowResourcePointer) => Promise<boolean>>;
let detail: jasmine.Spy;
let list: jasmine.Spy;
let getContext: jasmine.Spy;
let preview: jasmine.Spy;

function configure(): void {
  port = new WorkflowStepPort();
  keepDraft = jasmine.createSpy('keepDraft').and.resolveTo(true);
  port.connect(keepDraft);
  detail = jasmine
    .createSpy('detail')
    .and.callFake((_slug: string, id: string) => of({ status: 'found', asset: asset({ id }) }));
  list = jasmine
    .createSpy('list')
    .and.returnValue(of({ status: 'found', page: { items: [{ id: 'ctx-1' }, { id: 'ctx-2' }], nextCursor: null } }));
  getContext = jasmine.createSpy('get').and.callFake((_slug: string, id: string) =>
    of({
      status: 'found',
      context:
        id === 'ctx-1'
          ? context('ctx-1', 'Plum galette week', [
              keeper('img-1'),
              keeper('img-src', { purpose: 'Source' }),
              keeper('asset-ref', { kind: 'DamAsset', generatedImageId: null, mediaAssetId: 'asset-9' }),
            ])
          : context('ctx-2', null, [keeper('img-2')]),
    }),
  );
  preview = jasmine
    .createSpy('preview')
    .and.returnValue(of({ status: 'found', bytes: new Blob(['x'], { type: 'image/png' }) }));

  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: DamAssetService, useValue: { detail } },
      { provide: CreativeContextService, useValue: { list, get: getContext } },
      { provide: GeneratedImageService, useValue: { preview } },
      {
        provide: BrandProfileService,
        useValue: { listContentChannels: () => Promise.resolve({ status: 'found', channels: [] }) },
      },
    ],
  });
  TestBed.overrideComponent(AssetsAddStepComponent, {
    remove: {
      imports: [
        DamAssetUploadComponent,
        DamAssetPickerComponent,
        DamAssetVersionUploadComponent,
        DamLinkedAssetComponent,
        SaveToLibraryDialogComponent,
      ],
    },
    add: { imports: [StubUpload, StubPicker, StubVersionUpload, StubLinked, StubSave] },
  });
  TestBed.overrideComponent(AssetsDetailStepComponent, {
    remove: { imports: [DamAssetEditComponent] },
    add: { imports: [StubEdit] },
  });
  TestBed.overrideComponent(AssetsUseStepComponent, {
    remove: { imports: [DamAssetLogUseComponent, DamAssetUsageHistoryComponent] },
    add: { imports: [StubLogUse, StubHistory] },
  });
}

async function render<T>(type: Type<T>, inputs: Record<string, unknown>): Promise<ComponentFixture<T>> {
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

function text(fixture: ComponentFixture<unknown>): string {
  return (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function button(fixture: ComponentFixture<unknown>, label: string): HTMLButtonElement | null {
  return (
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find(
      (each) => each.textContent?.replace(/\s+/g, ' ').trim() === label,
    ) ?? null
  );
}

function stub<T>(fixture: ComponentFixture<unknown>, type: Type<T>): T {
  return fixture.debugElement.query((node) => node.componentInstance instanceof type).componentInstance;
}

async function chooseWay(fixture: ComponentFixture<unknown>, value: string): Promise<void> {
  const radio = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(`input[value="${value}"]`)!;
  radio.click();
  await settle(fixture);
}

describe('"Organize your pictures" step screens (13A.21)', () => {
  beforeEach(configure);

  describe('adding a picture', () => {
    it('opens on the picture a run started from, with its versions said, and nothing to choose', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: withPicture(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Version 2 is the one in use. Every earlier version is kept');
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-linked')?.textContent).toBe('asset-1');
      expect((fixture.nativeElement as HTMLElement).querySelector('cp-choice-group')).toBeNull();
      expect(port.registration()!.results!()).toEqual([{ kind: 'MediaAsset', id: 'asset-1' }]);
      expect(port.registration()!.ready!()).toBeTrue();
      expect(keepDraft).withContext('the run already holds it').not.toHaveBeenCalled();
    });

    it('opens on the picture the step kept as its draft, and does not keep it a second time', async () => {
      const run = decodeWorkflowRun(
        wireRun({
          id: 'run-1',
          definitionKey: 'assets.organize',
          links: [],
          steps: [
            {
              key: 'add',
              title: 'Add',
              requirement: 'Required',
              status: 'InProgress',
              draftKind: 'MediaAsset',
              draftResourceId: 'asset-3',
            },
          ],
        }),
      )!;
      const fixture = await render(AssetsAddStepComponent, { run, workspaceSlug: SLUG });

      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-linked')?.textContent).toBe('asset-3');
      expect(port.registration()!.results!()).toEqual([{ kind: 'MediaAsset', id: 'asset-3' }]);
      expect(keepDraft).not.toHaveBeenCalled();
    });

    it('offers three ways in on a run with no picture, and claims nothing made yet', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Upload a new picture');
      expect(text(fixture)).toContain('Choose one from your library');
      expect(text(fixture)).toContain('Save one you kept but haven’t saved yet');
      expect(port.registration()!.ready!()).toBeFalse();
      expect(port.registration()!.results!()).toEqual([]);
    });

    it('keeps an uploaded picture on the run as soon as it is in the library', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'upload');

      button(fixture, 'Upload a picture')!.click();
      await settle(fixture);
      expect(stub(fixture, StubUpload).open()).toBeTrue();
      expect(port.registration()!.unsaved()).withContext('an upload is running').toBeTrue();

      stub(fixture, StubUpload).uploaded.emit({ id: 'asset-new', title: 'My loaf' } as DamCreatedAsset);
      await settle(fixture);

      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'MediaAsset', id: 'asset-new' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'MediaAsset', id: 'asset-new' }]);
    });

    it('keeps a picture chosen from the library on the run', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'library');

      stub(fixture, StubPicker).chosen.emit({ id: 'asset-7', title: 'Old loaf' } as DamAssetSummary);
      await settle(fixture);

      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'MediaAsset', id: 'asset-7' });
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('finds pictures waiting to be saved on recent work, and only those', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'waiting');

      expect(list).toHaveBeenCalledOnceWith(SLUG, null, 10);
      expect(text(fixture)).toContain('From your 10 most recent pieces of work.');
      expect(text(fixture)).toContain('From “Plum galette week”');
      expect(text(fixture)).toContain('From “Untitled work”');
      const saves = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).filter(
        (each) => each.textContent?.trim() === 'Save this one',
      );
      expect(saves.length).withContext('a source picture and a saved one are not waiting').toBe(2);
    });

    it('saves a waiting picture on the work it was kept on, and keeps it on the run', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'waiting');

      (fixture.nativeElement as HTMLElement)
        .querySelector<HTMLButtonElement>('button[aria-label="Save this one: picture 1 from Plum galette week"]')!
        .click();
      await settle(fixture);
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-save')?.textContent).toBe('img-1 ctx-1');

      stub(fixture, StubSave).saved.emit({ id: 'asset-saved', title: 'Plum galette week' } as DamCreatedAsset);
      await settle(fixture);

      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'MediaAsset', id: 'asset-saved' });
    });

    it('leaves out a waiting picture the server no longer serves, and says when none is waiting', async () => {
      preview.and.callFake((_slug: string, id: string) =>
        of(id === 'img-1' ? { status: 'gone' } : { status: 'found', bytes: new Blob(['x']) }),
      );
      let fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'waiting');
      expect(text(fixture)).not.toContain('Plum galette week');
      expect(text(fixture)).toContain('From “Untitled work”');
      fixture.destroy();

      getContext.and.returnValue(of({ status: 'found', context: context('ctx-1', 'Nothing kept', []) }));
      fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'waiting');
      expect(text(fixture)).toContain('Nothing is waiting to be saved from your 10 most recent pieces of work.');
    });

    it('says recent work could not be read, with a way to read it again', async () => {
      list.and.returnValue(of({ status: 'unavailable' }));
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'waiting');
      expect(text(fixture)).toContain('Your recent work couldn’t be read just now.');

      list.and.returnValue(of({ status: 'found', page: { items: [], nextCursor: null } }));
      button(fixture, 'Try again')!.click();
      await settle(fixture);
      expect(text(fixture)).toContain('Nothing is waiting to be saved');
    });

    it('adds a new version beside the original and reads the picture again, also after a cancelled upload', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: withPicture(), workspaceSlug: SLUG });
      button(fixture, 'Add a new version')!.click();
      await settle(fixture);
      expect(stub(fixture, StubVersionUpload).open()).toBeTrue();

      detail.and.returnValue(
        of({ status: 'found', asset: asset({ currentVersion: { versionNumber: 3 } as DamAssetVersion }) }),
      );
      stub(fixture, StubVersionUpload).added.emit({ versionNumber: 3 } as DamAssetVersion);
      await settle(fixture);
      expect(text(fixture)).toContain('Version 3 is the one in use.');

      button(fixture, 'Add a new version')!.click();
      await settle(fixture);
      const reads = detail.calls.count();
      stub(fixture, StubVersionUpload).cancelled.emit();
      await settle(fixture);
      expect(detail.calls.count())
        .withContext('a cancelled upload may have landed')
        .toBe(reads + 1);
    });

    it('says the picture the run held is gone, and offers the ways in again', async () => {
      detail.and.returnValue(of({ status: 'not_found' }));
      const fixture = await render(AssetsAddStepComponent, { run: withPicture(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('isn’t in your library any more');
      expect(text(fixture)).toContain('Upload a new picture');
      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('says so when the picture could not be kept on the run, and still names it for Continue', async () => {
      keepDraft.and.resolveTo(false);
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'library');
      stub(fixture, StubPicker).chosen.emit({ id: 'asset-7', title: 'Old loaf' } as DamAssetSummary);
      await settle(fixture);

      expect(text(fixture)).toContain('this workflow couldn’t be pointed at it just now');
      expect(await port.registration()!.flush()).toBeTrue();
      expect(port.registration()!.results!()).toEqual([{ kind: 'MediaAsset', id: 'asset-7' }]);
    });
  });

  describe('describing and tagging', () => {
    it('describes on one step with where it came from, and tags on another', async () => {
      let fixture = await render(AssetsDetailStepComponent, {
        mode: 'describe',
        run: withPicture(),
        workspaceSlug: SLUG,
      });
      expect(stub(fixture, StubEdit).sections()).toBe('describe');
      expect(stub(fixture, StubEdit).inline()).toBeTrue();
      expect(text(fixture)).toContain('Where it came from');
      expect(text(fixture)).toContain('How it arrivedAI generated');
      expect(text(fixture)).toContain('Plum galette');
      fixture.destroy();

      fixture = await render(AssetsDetailStepComponent, { mode: 'tags', run: withPicture(), workspaceSlug: SLUG });
      expect(stub(fixture, StubEdit).sections()).toBe('tags');
      expect(text(fixture)).not.toContain('Where it came from');
    });

    it('saves on Continue, and stays when the save is refused', async () => {
      const fixture = await render(AssetsDetailStepComponent, {
        mode: 'describe',
        run: withPicture(),
        workspaceSlug: SLUG,
      });
      const form = stub(fixture, StubEdit);
      form.dirty.set(true);
      fixture.detectChanges();
      expect(port.registration()!.unsaved()).toBeTrue();

      expect(await port.registration()!.flush()).toBeTrue();
      form.saveAnswer = false;
      expect(await port.registration()!.flush()).toBeFalse();
      expect(form.saves).toBe(2);
    });

    it('says the picture is not there yet, or not any more, or could not be read', async () => {
      let fixture = await render(AssetsDetailStepComponent, {
        mode: 'describe',
        run: organizeRun([]),
        workspaceSlug: SLUG,
      });
      expect(text(fixture)).toContain('Go back to the first step to add or choose one.');
      fixture.destroy();

      detail.and.returnValue(of({ status: 'not_found' }));
      fixture = await render(AssetsDetailStepComponent, { mode: 'describe', run: withPicture(), workspaceSlug: SLUG });
      expect(text(fixture)).toContain('This picture isn’t in your library any more');
      fixture.destroy();

      detail.and.returnValue(of({ status: 'unavailable' }));
      fixture = await render(AssetsDetailStepComponent, { mode: 'tags', run: withPicture(), workspaceSlug: SLUG });
      expect(text(fixture)).toContain('Your picture couldn’t be read just now.');
      detail.and.callFake((_slug: string, id: string) => of({ status: 'found', asset: asset({ id }) }));
      button(fixture, 'Try again')!.click();
      await settle(fixture);
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-edit')).not.toBeNull();
    });
  });

  describe('noting a use', () => {
    it('logs what was entered on Continue, and reads the history again after a use is logged', async () => {
      const fixture = await render(AssetsUseStepComponent, { run: withPicture(), workspaceSlug: SLUG });
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-use')?.textContent).toBe('asset-1');

      expect(await port.registration()!.flush()).toBeTrue();
      expect(stub(fixture, StubLogUse).logs).toBe(1);

      stub(fixture, StubLogUse).logged.emit({ platformKey: 'instagram' });
      await settle(fixture);
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-history')?.textContent).toBe('1');
    });

    it('says the picture is not there yet', async () => {
      const fixture = await render(AssetsUseStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Go back to the first step to add or choose one.');
    });
  });

  // AU.7: what is on its way is drawn in its own shape and said once, from a status that is always there.
  describe('while something is read', () => {
    const root = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
    const statuses = (fixture: ComponentFixture<unknown>) =>
      Array.from(root(fixture).querySelectorAll('p.cp-sr-only[role="status"]')).map((each) => each.textContent!.trim());
    const shown = (fixture: ComponentFixture<unknown>, sentence: string) =>
      Array.from(root(fixture).querySelectorAll('p:not(.cp-sr-only)')).some((each) =>
        each.textContent?.includes(sentence),
      );

    it('draws the picture arriving, in its own shape, while the add step reads it', async () => {
      detail.and.returnValue(NEVER);
      const fixture = await render(AssetsAddStepComponent, { run: withPicture(), workspaceSlug: SLUG });

      expect(statuses(fixture)).toContain('Reading your picture…');
      expect(root(fixture).querySelector('[aria-busy="true"] cp-skeleton[data-shape="block"]')).not.toBeNull();
      expect(shown(fixture, 'Reading your picture…')).toBeFalse();
    });

    it('empties that status once the picture is read', async () => {
      const fixture = await render(AssetsAddStepComponent, { run: withPicture(), workspaceSlug: SLUG });

      expect(statuses(fixture)).toEqual(['']);
      expect(root(fixture).querySelector('cp-skeleton')).toBeNull();
    });

    it('draws recent work arriving while it is looked through, and empties the status once it is', async () => {
      list.and.returnValue(NEVER);
      const fixture = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(fixture, 'waiting');

      expect(statuses(fixture)).toContain('Looking through your recent work…');
      expect(root(fixture).querySelector('[aria-busy="true"] cp-skeleton')).not.toBeNull();
      expect(shown(fixture, 'Looking through your recent work…')).toBeFalse();
      fixture.destroy();

      list.and.returnValue(of({ status: 'found', page: { items: [], nextCursor: null } }));
      const read = await render(AssetsAddStepComponent, { run: organizeRun([]), workspaceSlug: SLUG });
      await chooseWay(read, 'waiting');
      expect(statuses(read).every((each) => each === '')).toBeTrue();
    });

    it('draws the picture arriving while the describing step reads it, and empties the status once it is', async () => {
      detail.and.returnValue(NEVER);
      const fixture = await render(AssetsDetailStepComponent, {
        mode: 'describe',
        run: withPicture(),
        workspaceSlug: SLUG,
      });

      expect(statuses(fixture)).toEqual(['Reading your picture…']);
      expect(root(fixture).querySelector('[aria-busy="true"] cp-skeleton[data-shape="block"]')).not.toBeNull();
      expect(shown(fixture, 'Reading your picture…')).toBeFalse();
      fixture.destroy();

      detail.and.callFake((_slug: string, id: string) => of({ status: 'found', asset: asset({ id }) }));
      const read = await render(AssetsDetailStepComponent, {
        mode: 'describe',
        run: withPicture(),
        workspaceSlug: SLUG,
      });
      expect(statuses(read)).toEqual(['']);
    });
  });
});
