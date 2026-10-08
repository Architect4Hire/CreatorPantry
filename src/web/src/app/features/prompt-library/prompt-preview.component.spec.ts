import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of } from 'rxjs';

import { ClipboardService } from '../../core/clipboard.service';
import { PromptDetail, PromptSummary } from '../../models/prompt-library.models';
import { PromptDetailOutcome, PromptLibraryService } from '../../services/prompt-library.service';
import { PromptPreviewComponent } from './prompt-preview.component';

function summary(id: string, label: string | null): PromptSummary {
  return {
    promptRecordId: id,
    channelKey: 'instagram',
    imageKind: 'Hero',
    textPreview: 'A tight crop',
    textLength: 400,
    label,
    source: 'Manual',
    recipeId: null,
    recipeVersionId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
  };
}

function detail(id: string, text: string): PromptDetail {
  return {
    promptRecordId: id,
    channelKey: 'instagram',
    imageKind: 'Hero',
    text,
    generatedText: null,
    label: null,
    source: 'Manual',
    aiProposalId: null,
    recipeId: null,
    recipeVersionId: null,
    promptTemplateId: null,
    promptTemplateVersion: null,
    promptTemplateBodyChecksum: null,
    createdAt: '2026-10-08T12:00:00+00:00',
  };
}

function foundPrompt(id: string, text: string): Observable<PromptDetailOutcome> {
  return of<PromptDetailOutcome>({ status: 'found', prompt: detail(id, text) });
}

@Component({
  standalone: true,
  imports: [PromptPreviewComponent],
  template: `<cp-prompt-preview [workspaceSlug]="slug()" [summary]="summary()" (closed)="closes = closes + 1" />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly summary = signal<PromptSummary | null>(null);
  closes = 0;
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('PromptPreviewComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let getSpy: jasmine.Spy<(slug: string, id: string) => Observable<PromptDetailOutcome>>;
  let copySpy: jasmine.Spy<(text: string) => Promise<boolean>>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function dialog(): HTMLElement | null {
    return root().querySelector('[role="dialog"]');
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  async function create(): Promise<void> {
    copySpy = jasmine.createSpy('copy').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([]),
        { provide: PromptLibraryService, useValue: { get: getSpy } },
        { provide: ClipboardService, useValue: { copy: copySpy } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    await settle();
  }

  async function open(row: PromptSummary): Promise<void> {
    fixture.componentInstance.summary.set(row);
    await settle();
  }

  it('renders nothing and reads nothing while closed', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt('p1', 'x'));
    await create();

    expect(dialog()).toBeNull();
    expect(getSpy).not.toHaveBeenCalled();
  });

  it('opens as a modal dialog named by the prompt, with focus inside it', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt('p1', 'x'));
    await create();
    await open(summary('p1', 'Chili hero'));

    const shown = dialog() as HTMLElement;
    expect(shown.getAttribute('aria-modal')).toBe('true');
    const titleId = shown.getAttribute('aria-labelledby') as string;
    expect(root().querySelector(`#${titleId}`)?.textContent).toContain('Chili hero');
    expect(document.activeElement).toBe(shown);
  });

  it('says it is loading, then shows the whole prompt rather than the truncated row', async () => {
    const pending = new Subject<PromptDetailOutcome>();
    getSpy = jasmine.createSpy('get').and.returnValue(pending);
    await create();
    await open(summary('p1', 'Chili hero'));

    expect(getSpy).toHaveBeenCalledOnceWith('cozy-fall', 'p1');
    expect(dialog()?.querySelector('[role="status"]')?.textContent).toContain('Loading the prompt');
    expect(button('Copy prompt').disabled).toBeTrue();

    pending.next({ status: 'found', prompt: detail('p1', 'The whole prompt,\nwith its second line.') });
    await settle();

    expect(dialog()?.querySelector('.prompt-text')?.textContent).toBe('The whole prompt,\nwith its second line.');
    expect(button('Copy prompt').disabled).toBeFalse();
  });

  it('copies the full text, and says so in a live region', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt('p1', 'Every word of it.'));
    await create();
    await open(summary('p1', 'Chili hero'));

    const status = root().querySelector('.copy-status') as HTMLElement;
    // Present before it has anything to say, which is what makes the announcement reliable.
    expect(status.getAttribute('role')).toBe('status');
    expect(status.textContent?.trim()).toBe('');

    button('Copy prompt').click();
    await settle();

    expect(copySpy).toHaveBeenCalledOnceWith('Every word of it.');
    expect(status.textContent).toContain('Prompt copied.');
  });

  it('says so when the copy did not happen, instead of claiming it did', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt('p1', 'Every word of it.'));
    await create();
    copySpy.and.resolveTo(false);
    await open(summary('p1', 'Chili hero'));

    button('Copy prompt').click();
    await settle();

    expect(root().querySelector('.copy-status')?.textContent).toContain("couldn't be copied");
    expect(root().querySelector('.copy-status')?.textContent).not.toContain('Prompt copied.');
  });

  it('reports a prompt that is not there, and offers nothing to copy', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(of<PromptDetailOutcome>({ status: 'not_found' }));
    await create();
    await open(summary('p1', 'Chili hero'));

    expect(dialog()?.querySelector('[role="alert"]')?.textContent).toContain("couldn't find this prompt");
    expect(button('Copy prompt').disabled).toBeTrue();
  });

  it('reports a failed read with a retry that reads again', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(of<PromptDetailOutcome>({ status: 'unavailable' }));
    await create();
    await open(summary('p1', 'Chili hero'));

    expect(dialog()?.querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");

    getSpy.and.returnValue(foundPrompt('p1', 'Here now.'));
    button('Try again').click();
    await settle();

    expect(getSpy).toHaveBeenCalledTimes(2);
    expect(dialog()?.textContent).toContain('Here now.');
  });

  it('links to the full detail page of this workspace', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt('p1', 'x'));
    await create();
    await open(summary('p1', 'Chili hero'));

    const link = Array.from(root().querySelectorAll('a')).find((each) => each.textContent?.includes('Open full details'));
    expect(link?.getAttribute('href')).toBe('/cozy-fall/prompt-library/p1');
  });

  it('asks to close on Escape and on its close button', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt('p1', 'x'));
    await create();
    await open(summary('p1', 'Chili hero'));

    (dialog() as HTMLElement).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(fixture.componentInstance.closes).toBe(1);

    (root().querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement).click();
    expect(fixture.componentInstance.closes).toBe(2);
  });

  it("never shows one prompt's words under another: a slow read is dropped when the row changes", async () => {
    const slow = new Subject<PromptDetailOutcome>();
    getSpy = jasmine.createSpy('get').and.callFake((_slug: string, id: string) =>
      id === 'p1' ? slow : foundPrompt('p2', 'Second prompt.'),
    );
    await create();
    await open(summary('p1', 'First'));
    await open(summary('p2', 'Second'));

    slow.next({ status: 'found', prompt: detail('p1', 'First prompt.') });
    await settle();

    expect(dialog()?.textContent).toContain('Second prompt.');
    expect(dialog()?.textContent).not.toContain('First prompt.');
  });

  it('reads again, for the new workspace, when the workspace changes under an open preview', async () => {
    getSpy = jasmine.createSpy('get').and.callFake((slug: string) =>
      slug === 'cozy-fall' ? foundPrompt('p1', 'Workspace A words.') : new Subject<PromptDetailOutcome>(),
    );
    await create();
    await open(summary('p1', 'First'));
    expect(dialog()?.textContent).toContain('Workspace A words.');

    fixture.componentInstance.slug.set('other-kitchen');
    await settle();

    expect(getSpy.calls.mostRecent().args).toEqual(['other-kitchen', 'p1']);
    expect(dialog()?.textContent).not.toContain('Workspace A words.');
  });
});
