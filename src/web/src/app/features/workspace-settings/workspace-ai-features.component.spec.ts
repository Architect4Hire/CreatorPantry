import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AiFeature } from '../../models/ai-feature.models';
import { AiFeaturesOutcome, AiFeaturesService, SaveAiFeaturesOutcome } from '../../services/ai-features.service';
import { WorkspaceAiFeaturesComponent } from './workspace-ai-features.component';

const TASKS = ['recipe.concepts', 'recipe.revision', 'content.seo-package', 'future.task'];

function features(enabled: readonly string[], setByWorkspace = true): AiFeature[] {
  return TASKS.map((task) => ({ task, enabled: enabled.includes(task), setByWorkspace }));
}

class StubService {
  getOutcome: AiFeaturesOutcome = { status: 'found', features: features(['recipe.concepts']) };
  saveOutcome: SaveAiFeaturesOutcome | null = null;
  readonly saves: { slug: string; enabled: readonly string[] }[] = [];
  reads = 0;
  /** Set to leave the read unanswered, so the card stays loading. */
  hold = false;

  get(): Promise<AiFeaturesOutcome> {
    this.reads += 1;
    if (this.hold) return new Promise(() => undefined);
    return Promise.resolve(this.getOutcome);
  }

  replace(slug: string, enabled: readonly string[]): Promise<SaveAiFeaturesOutcome> {
    this.saves.push({ slug, enabled });
    return Promise.resolve(this.saveOutcome ?? { status: 'saved', features: features(enabled) });
  }
}

describe('WorkspaceAiFeaturesComponent', () => {
  let fixture: ComponentFixture<WorkspaceAiFeaturesComponent>;
  let service: StubService;

  async function render(canEdit = true): Promise<HTMLElement> {
    fixture = TestBed.createComponent(WorkspaceAiFeaturesComponent);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('canEdit', canEdit);
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function box(task: string): HTMLInputElement {
    return document.getElementById(`cp-ai-feature-${task}`) as HTMLInputElement;
  }

  function saveButton(host: HTMLElement): HTMLButtonElement {
    return host.querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  async function toggle(task: string): Promise<void> {
    box(task).click();
    await settle();
  }

  beforeEach(() => {
    service = new StubService();
    TestBed.configureTestingModule({
      imports: [WorkspaceAiFeaturesComponent],
      providers: [{ provide: AiFeaturesService, useValue: service }],
    });
  });

  it('draws its form arriving while the features are read, said once from a region that is always there (AU.6.4)', async () => {
    service.hold = true;

    const host = await render();

    const status = host.querySelector('p.cp-sr-only[role="status"]')!;
    expect(status.textContent).toBe('Loading your AI features…');
    const busy = host.querySelector('.loading[aria-busy="true"]')!;
    expect(busy.querySelectorAll('cp-skeleton').length).toBe(2);
    expect(busy.textContent!.trim()).toBe('');
    expect(host.querySelector('form')).toBeNull();
  });

  it('empties that status once the features are read', async () => {
    const host = await render();

    expect(host.querySelector('p.cp-sr-only[role="status"]')!.textContent).toBe('');
  });

  it('shows each feature under its area, checked when it is on, with nothing to save', async () => {
    const host = await render();

    expect(box('recipe.concepts').checked).toBeTrue();
    expect(box('recipe.revision').checked).toBeFalse();
    expect(box('content.seo-package').checked).toBeFalse();
    expect(host.textContent).toContain('Recipe revision');
    expect(host.textContent).toContain('SEO package');
    expect(saveButton(host).disabled).toBeTrue();
    expect(host.textContent).not.toContain('Unsaved changes');
  });

  it('still offers a feature this client has no words for, under its own name', async () => {
    const host = await render();

    expect(box('future.task')).not.toBeNull();
    expect(host.textContent).toContain('future.task');
  });

  it('says the settings are the starting ones until an Owner has saved', async () => {
    service.getOutcome = { status: 'found', features: features(['recipe.concepts'], false) };
    expect((await render()).textContent).toContain('starting settings');

    service.getOutcome = { status: 'found', features: features(['recipe.concepts'], true) };
    expect((await render()).textContent).not.toContain('starting settings');
  });

  it('saves the whole picked set across areas, then reads as clean', async () => {
    const host = await render();

    await toggle('recipe.revision');
    await toggle('content.seo-package');

    expect(host.textContent).toContain('Unsaved changes');
    expect(saveButton(host).disabled).toBeFalse();

    saveButton(host).click();
    await settle();

    expect(service.saves.length).toBe(1);
    expect(service.saves[0].slug).toBe('sams-kitchen');
    expect([...service.saves[0].enabled].sort()).toEqual(['content.seo-package', 'recipe.concepts', 'recipe.revision']);
    expect(host.textContent).toContain('Saved.');
    expect(host.textContent).not.toContain('Unsaved changes');
    expect(saveButton(host).disabled).toBeTrue();
  });

  it('is clean again when a change is undone, and sends nothing', async () => {
    const host = await render();

    await toggle('recipe.revision');
    await toggle('recipe.revision');

    expect(saveButton(host).disabled).toBeTrue();
    expect(service.saves.length).toBe(0);
  });

  it('keeps the picks and says why when a save fails', async () => {
    for (const [outcome, message] of [
      [{ status: 'unavailable' }, 'couldn’t save'],
      [{ status: 'forbidden' }, 'Only an Owner'],
      [{ status: 'conflict' }, 'Someone else changed'],
    ] as const) {
      service.saveOutcome = outcome;
      const host = await render();

      await toggle('recipe.revision');
      saveButton(host).click();
      await settle();

      expect(host.querySelector('.result')!.textContent).withContext(outcome.status).toContain(message);
      expect(box('recipe.revision').checked).withContext(outcome.status).toBeTrue();
      expect(saveButton(host).disabled).withContext(outcome.status).toBeFalse();
    }
  });

  it('clears a save message as soon as the picks change again', async () => {
    const host = await render();

    await toggle('recipe.revision');
    saveButton(host).click();
    await settle();
    expect(host.textContent).toContain('Saved.');

    await toggle('recipe.revision');
    expect(host.textContent).not.toContain('Saved.');
  });

  it('tells a member who is not an Owner what is on, in words, with no controls', async () => {
    const host = await render(false);

    expect(host.querySelector('input')).toBeNull();
    expect(saveButton(host)).toBeNull();
    expect(host.textContent).toContain('Recipe concepts');
    expect(host.textContent).toContain('Nothing switched on');
    expect(host.textContent).toContain('Only an Owner of this workspace can change this.');
  });

  it('offers a retry when the features cannot be loaded, and loads on it', async () => {
    service.getOutcome = { status: 'unavailable' };
    const host = await render();

    expect(host.querySelector('[role="alert"]')!.textContent).toContain("couldn't be loaded");

    service.getOutcome = { status: 'found', features: features([]) };
    (host.querySelector('button') as HTMLButtonElement).click();
    await settle();

    expect(service.reads).toBe(2);
    expect(box('recipe.concepts')).not.toBeNull();
  });

  it('says so when the workspace is not there', async () => {
    service.getOutcome = { status: 'not_found' };

    expect((await render()).textContent).toContain("couldn't find this workspace");
  });
});
