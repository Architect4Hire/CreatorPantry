import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';

import { Workspace, WorkspaceMeasurementSystem } from '../../models/workspace.models';
import {
  SaveMeasurementPreferenceOutcome,
  WorkspaceOutcome,
  WorkspaceSettingsService,
} from '../../services/workspace-settings.service';
import { WorkspaceSettingsComponent } from './workspace-settings.component';

function workspace(overrides: Partial<Workspace> = {}): Workspace {
  return {
    workspaceId: 'w1',
    name: "Sam's Kitchen",
    slug: 'sams-kitchen',
    role: 'Owner',
    defaultMeasurementSystem: 'UsCustomary',
    ...overrides,
  };
}

class StubSettings {
  getOutcome: WorkspaceOutcome = { status: 'found', workspace: workspace() };
  saveOutcome: SaveMeasurementPreferenceOutcome | null = null;
  readonly saves: { slug: string; system: WorkspaceMeasurementSystem }[] = [];

  get(): Promise<WorkspaceOutcome> {
    return Promise.resolve(this.getOutcome);
  }

  setMeasurementSystem(slug: string, system: WorkspaceMeasurementSystem): Promise<SaveMeasurementPreferenceOutcome> {
    this.saves.push({ slug, system });
    return Promise.resolve(
      this.saveOutcome ?? { status: 'saved', workspace: workspace({ defaultMeasurementSystem: system }) },
    );
  }
}

describe('WorkspaceSettingsComponent', () => {
  let fixture: ComponentFixture<WorkspaceSettingsComponent>;
  let settings: StubSettings;

  async function render(): Promise<HTMLElement> {
    fixture = TestBed.createComponent(WorkspaceSettingsComponent);
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function radio(host: HTMLElement, value: string): HTMLInputElement {
    return host.querySelector(`#cp-measurement-system-${value}`) as HTMLInputElement;
  }

  function saveButton(host: HTMLElement): HTMLButtonElement {
    return host.querySelector('button[type="submit"]') as HTMLButtonElement;
  }

  beforeEach(() => {
    settings = new StubSettings();
    TestBed.configureTestingModule({
      imports: [WorkspaceSettingsComponent],
      providers: [
        { provide: WorkspaceSettingsService, useValue: settings },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ workspaceSlug: 'sams-kitchen' }) }, parent: null },
        },
      ],
    });
  });

  it('shows the workspace’s current system picked, with nothing to save', async () => {
    settings.getOutcome = { status: 'found', workspace: workspace({ defaultMeasurementSystem: 'Metric' }) };

    const host = await render();

    expect(radio(host, 'Metric').checked).toBeTrue();
    expect(radio(host, 'UsCustomary').checked).toBeFalse();
    expect(saveButton(host).disabled).toBeTrue();
  });

  it('saves the picked system to the route’s workspace and then reads as clean', async () => {
    const host = await render();

    radio(host, 'Metric').click();
    await settle();
    expect(saveButton(host).disabled).toBeFalse();
    expect(host.textContent).toContain('Unsaved change');

    saveButton(host).click();
    await settle();

    expect(settings.saves).toEqual([{ slug: 'sams-kitchen', system: 'Metric' }]);
    expect(host.textContent).toContain('New drafts will be written in Metric');
    expect(host.textContent).not.toContain('Unsaved change');
    expect(saveButton(host).disabled).toBeTrue();
  });

  it('keeps the pick and says so when the save fails', async () => {
    settings.saveOutcome = { status: 'unavailable' };
    const host = await render();

    radio(host, 'Metric').click();
    await settle();
    saveButton(host).click();
    await settle();

    expect(host.textContent).toContain('couldn’t save');
    expect(radio(host, 'Metric').checked).toBeTrue();
    expect(saveButton(host).disabled).toBeFalse();
  });

  it('tells a member who is not an Owner the answer without offering the controls', async () => {
    settings.getOutcome = {
      status: 'found',
      workspace: workspace({ role: 'Editor', defaultMeasurementSystem: 'Metric' }),
    };

    const host = await render();

    expect(host.querySelector('input')).toBeNull();
    expect(host.querySelector('button[type="submit"]')).toBeNull();
    expect(host.textContent).toContain('This workspace works in Metric');
    expect(host.textContent).toContain('Only an Owner');
  });

  it('offers a retry when the settings cannot be loaded', async () => {
    settings.getOutcome = { status: 'unavailable' };

    const host = await render();

    expect(host.querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");
    settings.getOutcome = { status: 'found', workspace: workspace() };
    (host.querySelector('button') as HTMLButtonElement).click();
    await settle();

    expect(radio(host, 'UsCustomary').checked).toBeTrue();
  });

  it('says so when the workspace cannot be found', async () => {
    settings.getOutcome = { status: 'not_found' };

    const host = await render();

    expect(host.querySelector('[role="alert"]')?.textContent).toContain("couldn't find this workspace");
  });
});
