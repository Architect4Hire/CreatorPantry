import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { DamAssetDetail, DamAssetUtilization } from '../../models/dam-asset.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { DamAssetEditComponent } from './dam-asset-edit.component';
import { DamAssetLogUseComponent } from './dam-asset-log-use.component';

function asset(overrides: Partial<DamAssetDetail> = {}): DamAssetDetail {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    description: 'Overhead.',
    altText: 'A round loaf on linen.',
    kind: 'Original',
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    rightsHolder: null,
    attributionText: null,
    tags: [{ id: 't1', name: 'Weeknight' }],
    currentVersion: null,
    versions: [],
    utilizationCount: 0,
    recipeLinks: [],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [],
    sourceGeneratedImageId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    concurrencyToken: 'token-1',
    ...overrides,
  };
}

@Component({
  standalone: true,
  imports: [DamAssetEditComponent, DamAssetLogUseComponent],
  template: `
    @if (which() === 'edit') {
      <cp-dam-asset-edit
        [inline]="true"
        [sections]="sections()"
        workspaceSlug="cozy-fall"
        [asset]="asset()"
        (saved)="saved.push($event)"
      />
    } @else {
      <cp-dam-asset-log-use [inline]="true" workspaceSlug="cozy-fall" assetId="a1" (logged)="logged.push($event)" />
    }
  `,
})
class HostComponent {
  readonly which = signal<'edit' | 'use'>('edit');
  readonly sections = signal<'describe' | 'tags'>('describe');
  readonly asset = signal(asset());
  readonly saved: DamAssetDetail[] = [];
  readonly logged: DamAssetUtilization[] = [];
  readonly edit = viewChild(DamAssetEditComponent);
  readonly use = viewChild(DamAssetLogUseComponent);
}

describe('The library’s forms, inline on a guided step (13A.21)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let patch: jasmine.Spy;
  let log: jasmine.Spy;

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
    fixture.detectChanges();
  }

  async function mount(which: 'edit' | 'use', sections: 'describe' | 'tags' = 'describe'): Promise<void> {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.which.set(which);
    host.sections.set(sections);
    await settle();
  }

  function el(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  async function type(id: string, value: string): Promise<void> {
    const field = el().querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`)!;
    field.value = value;
    field.dispatchEvent(new Event('input'));
    await settle();
  }

  beforeEach(() => {
    patch = jasmine.createSpy('patchMetadata');
    log = jasmine.createSpy('logUtilization');

    TestBed.configureTestingModule({
      providers: [
        {
          provide: DamAssetService,
          useValue: {
            patchMetadata: patch,
            logUtilization: log,
            detail: () => of({ status: 'found', asset: asset({ title: 'Theirs', concurrencyToken: 'token-2' }) }),
          },
        },
        {
          provide: WorkspaceTagService,
          useValue: {
            list: () =>
              Promise.resolve({
                status: 'found',
                tags: [
                  { id: 't1', name: 'Weeknight' },
                  { id: 't2', name: 'Baking' },
                ],
              }),
          },
        },
        { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(true) } },
      ],
    });
  });

  describe('the edit form', () => {
    it('draws in place, with no drawer and no legend of its own, and only the part the step is about', async () => {
      await mount('edit', 'describe');

      expect(el().querySelector('[role="dialog"]')).toBeNull();
      expect(el().querySelector('.legend')).toBeNull();
      expect(el().querySelector('#cp-dam-edit-title')).not.toBeNull();
      expect(el().querySelector('#cp-dam-edit-rights-holder')).not.toBeNull();
      expect(el().textContent).not.toContain('Tags come from this workspace');

      fixture.destroy();
      await mount('edit', 'tags');
      expect(el().querySelector('#cp-dam-edit-title')).toBeNull();
      expect(el().textContent).toContain('Tags come from this workspace');
    });

    it('lets a step move on without a request when nothing changed', async () => {
      await mount('edit');

      expect(await host.edit()!.saveForStep()).toBeTrue();
      expect(patch).not.toHaveBeenCalled();
    });

    it('saves only what changed on Continue, quoting the token it read, and stays open on what the server answered', async () => {
      patch.and.resolveTo({
        status: 'updated',
        asset: asset({ altText: 'A torn loaf.', concurrencyToken: 'token-2' }),
      });
      await mount('edit');
      await type('cp-dam-edit-alt-text', 'A torn loaf.');
      expect(host.edit()!.dirty()).toBeTrue();

      expect(await host.edit()!.saveForStep()).toBeTrue();
      await settle();

      const [, , body] = patch.calls.mostRecent().args;
      expect(body).toEqual({ expectedConcurrencyToken: 'token-1', altText: 'A torn loaf.' });
      expect(host.saved.length).toBe(1);
      expect(host.edit()!.dirty()).withContext('the form now stands on the saved values').toBeFalse();
      expect(el().querySelector('#cp-dam-edit-alt-text')).withContext('still open').not.toBeNull();

      // The next save quotes the token the server gave.
      patch.and.resolveTo({
        status: 'updated',
        asset: asset({ altText: 'A torn loaf, again.', concurrencyToken: 'token-3' }),
      });
      await type('cp-dam-edit-alt-text', 'A torn loaf, again.');
      await host.edit()!.saveForStep();
      expect(patch.calls.mostRecent().args[2]['expectedConcurrencyToken']).toBe('token-2');
    });

    it('keeps the creator on the form, and every word typed, when someone else changed the picture', async () => {
      patch.and.resolveTo({ status: 'stale' });
      await mount('edit');
      await type('cp-dam-edit-alt-text', 'Mine.');

      expect(await host.edit()!.saveForStep()).toBeFalse();
      await settle();

      expect(el().textContent).toContain('Someone else changed this picture while you were editing');
      expect(el().querySelector<HTMLTextAreaElement>('#cp-dam-edit-alt-text')!.value).toBe('Mine.');
      expect(await host.edit()!.saveForStep())
        .withContext('not again until the latest is loaded')
        .toBeFalse();
      expect(patch).toHaveBeenCalledTimes(1);
    });

    it('says a refused field beside it, and does not move on', async () => {
      patch.and.resolveTo({ status: 'invalid', message: 'No.', fieldErrors: { altText: ['Alt text is too long.'] } });
      await mount('edit');
      await type('cp-dam-edit-alt-text', 'Mine.');

      expect(await host.edit()!.saveForStep()).toBeFalse();
      await settle();

      expect(el().textContent).toContain('Alt text is too long.');
    });

    it('does not send a title emptied by the creator, and says so beside it', async () => {
      await mount('edit');
      await type('cp-dam-edit-title', '   ');

      expect(await host.edit()!.saveForStep()).toBeFalse();
      expect(patch).not.toHaveBeenCalled();
    });
  });

  describe('the log-a-use form', () => {
    it('lets a step move on without logging anything when nothing was entered', async () => {
      await mount('use');

      expect(el().querySelector('[role="dialog"]')).toBeNull();
      expect(await host.use()!.logForStep()).toBeTrue();
      expect(log).not.toHaveBeenCalled();
    });

    it('logs what was entered on Continue, once, and empties for the next use', async () => {
      log.and.resolveTo({
        status: 'logged',
        use: {
          id: 'u1',
          platformKey: 'instagram',
          utilizedOn: '2026-10-15',
          utilizedDay: 'Thursday',
          campaignName: null,
          notes: null,
        },
      });
      await mount('use');
      await type('cp-dam-use-platform', 'instagram');

      expect(await host.use()!.logForStep()).toBeTrue();
      await settle();

      expect(log).toHaveBeenCalledTimes(1);
      expect(host.logged.length).toBe(1);
      expect(el().querySelector<HTMLInputElement>('#cp-dam-use-platform')!.value).toBe('');
      expect(await host.use()!.logForStep())
        .withContext('nothing left to log')
        .toBeTrue();
      expect(log).toHaveBeenCalledTimes(1);
    });

    it('retries a lost answer under the same key, so one use is not logged twice', async () => {
      log.and.resolveTo({ status: 'unavailable' });
      await mount('use');
      await type('cp-dam-use-platform', 'instagram');

      expect(await host.use()!.logForStep()).toBeFalse();
      expect(await host.use()!.logForStep()).toBeFalse();

      expect(log.calls.argsFor(1)[3]).toBe(log.calls.argsFor(0)[3]);
    });

    it('asks for a platform before logging, and does not move on', async () => {
      await mount('use');
      await type('cp-dam-use-campaign', 'Autumn');

      expect(await host.use()!.logForStep()).toBeFalse();
      expect(log).not.toHaveBeenCalled();
    });
  });
});
