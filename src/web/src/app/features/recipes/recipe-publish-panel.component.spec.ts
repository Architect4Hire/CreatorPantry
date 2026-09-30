import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RecipeExportSummary } from '../../models/recipe-export.models';
import {
  RecipeExportFormat,
  RecipeExportService,
  RecipeExportSummaryOutcome,
} from '../../services/recipe-export.service';
import { RecipePublishPanelComponent } from './recipe-publish-panel.component';

const GATEWAY = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/exports';

function summary(overrides: Partial<RecipeExportSummary> = {}): RecipeExportSummary {
  return {
    versionNumber: 3,
    exportable: true,
    notExportableReason: null,
    editorial: { revisionNumber: 2, isCurrent: true },
    seo: { revisionNumber: 1, isCurrent: true },
    ...overrides,
  };
}

class FakeExportService {
  outcomes: RecipeExportSummaryOutcome[] = [{ status: 'found', summary: summary() }];
  calls = 0;
  linksAvailable = true;
  pending: Array<(outcome: RecipeExportSummaryOutcome) => void> = [];

  async getSummary(): Promise<RecipeExportSummaryOutcome> {
    this.calls += 1;
    const outcome = this.outcomes.shift();
    return outcome ?? { status: 'unavailable' };
  }

  downloadUrl(
    _slug: string,
    _recipeId: string,
    format: RecipeExportFormat,
    versionNumber: number,
    choices: { template: string; units: string; pageSize: string },
  ): string | null {
    return this.linksAvailable
      ? `${GATEWAY}/${format}?v=${versionNumber}&t=${choices.template}&u=${choices.units}&p=${choices.pageSize}`
      : null;
  }
}

describe('RecipePublishPanelComponent', () => {
  let fixture: ComponentFixture<RecipePublishPanelComponent>;
  let service: FakeExportService;

  async function create(versionHint: number | null = 3): Promise<void> {
    fixture = TestBed.createComponent(RecipePublishPanelComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('versionHint', versionHint);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const el = (): HTMLElement => fixture.nativeElement;
  const text = (): string => el().textContent?.replace(/\s+/g, ' ') ?? '';
  const links = (): HTMLAnchorElement[] => Array.from(el().querySelectorAll<HTMLAnchorElement>('a[href]'));

  beforeEach(() => {
    service = new FakeExportService();
    TestBed.configureTestingModule({
      imports: [RecipePublishPanelComponent],
      providers: [{ provide: RecipeExportService, useValue: service }],
    });
  });

  describe('states', () => {
    it('says it is checking while the summary loads', async () => {
      fixture = TestBed.createComponent(RecipePublishPanelComponent);
      fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
      fixture.componentRef.setInput('recipeId', 'r1');
      fixture.detectChanges();

      expect(text()).toContain('Checking what can be exported');
      expect(links().length).toBe(0);
      await fixture.whenStable();
    });

    it('shows the source version, readiness and both accepted revisions when ready', async () => {
      await create();

      expect(text()).toContain('Version 3');
      expect(text()).toContain('Ready to export');
      expect(text()).toContain('Editorial copy');
      expect(text()).toContain('Revision 2');
      expect(text()).toContain('SEO copy');
      expect(text()).toContain('Revision 1');
    });

    it('is an error with a retry when the summary cannot be read, and recovers on retry', async () => {
      service.outcomes = [{ status: 'unavailable' }, { status: 'found', summary: summary() }];
      await create();

      expect(el().querySelector('[role="alert"]')?.textContent).toContain("Couldn't check what can be exported");
      expect(links().length).toBe(0);

      Array.from(el().querySelectorAll('button')).find((b) => b.textContent?.includes('Try again'))!.click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(text()).toContain('Ready to export');
    });

    it('is the same "not found" for an unknown and a foreign recipe, with no downloads', async () => {
      service.outcomes = [{ status: 'not_found' }];
      await create();

      expect(text()).toContain('This recipe could not be found');
      expect(links().length).toBe(0);
    });

    it('is empty-but-useful when nothing is accepted: each row says what will be missing', async () => {
      service.outcomes = [{ status: 'found', summary: summary({ editorial: null, seo: null }) }];
      await create();

      expect(text()).toContain('Nothing accepted');
      expect(text()).toContain('Nothing is accepted yet, so Markdown and PDF, standard layout will carry none.');
      // Downloads stay available: the copy is optional. JSON-LD also has a preview, hence four links.
      expect(links().length).toBe(4);
    });

    it('is degraded, with a stated reason, when the gateway address is not known', async () => {
      service.linksAvailable = false;
      await create();

      expect(text()).toContain('Downloads aren\'t available right now');
      expect(links().length).toBe(0);
      expect(text()).toContain('Version 3');
    });
  });

  describe('readiness', () => {
    it('disables every download and explains why when the version is not approved', async () => {
      service.outcomes = [
        { status: 'found', summary: summary({ exportable: false, notExportableReason: 'recipe_export_not_approved' }) },
      ];
      await create();

      expect(text()).toContain('Not approved');
      expect(text()).toContain('not approved or marked ready');
      expect(links().length).toBe(0);
      const buttons = Array.from(el().querySelectorAll<HTMLButtonElement>('.download-actions button'));
      expect(buttons.length).toBe(3);
      expect(buttons.every((button) => button.disabled)).toBeTrue();
    });

    it('never shows the raw reason code', async () => {
      service.outcomes = [
        { status: 'found', summary: summary({ exportable: false, notExportableReason: 'something_new' }) },
      ];
      await create();

      expect(text()).toContain('cannot be exported right now');
      expect(text()).not.toContain('something_new');
    });
  });

  describe('staleness', () => {
    it('marks stale copy as needing review and says it is left out', async () => {
      service.outcomes = [
        { status: 'found', summary: summary({ editorial: { revisionNumber: 2, isCurrent: false } }) },
      ];
      await create();

      expect(text()).toContain('Needs review');
      expect(text()).toContain('is left out of Markdown and PDF, standard layout until you review it');
      // The other copy is unaffected.
      expect(text()).toContain('Revision 1 is included in structured data');
    });

    it('conveys staleness with words, not only the pill colour', async () => {
      service.outcomes = [{ status: 'found', summary: summary({ seo: { revisionNumber: 1, isCurrent: false } }) }];
      await create();

      const row = el().querySelectorAll('.copy-row')[1];
      expect(row.textContent).toContain('Needs review');
      expect(row.textContent).toContain('left out');
    });
  });

  describe('downloads', () => {
    it('offers JSON-LD, Markdown and PDF as links to the gateway, pinned to the summarised version', async () => {
      await create();

      const hrefs = links().map((a) => a.getAttribute('href')!);
      expect(hrefs.some((h) => h.startsWith(`${GATEWAY}/json-ld?v=3`))).toBeTrue();
      expect(hrefs.some((h) => h.startsWith(`${GATEWAY}/markdown?v=3`))).toBeTrue();
      expect(hrefs.some((h) => h.startsWith(`${GATEWAY}/pdf?v=3`))).toBeTrue();
      expect(hrefs.every((h) => h.startsWith(GATEWAY))).toBeTrue();
    });

    it('marks downloads as downloads, and opens only the JSON-LD preview in a new tab safely', async () => {
      await create();

      const downloads = links().filter((a) => a.hasAttribute('download'));
      expect(downloads.length).toBe(3);

      const previews = links().filter((a) => a.target === '_blank');
      expect(previews.length).toBe(1);
      expect(previews[0].getAttribute('href')).toContain('/json-ld');
      expect(previews[0].rel).toContain('noopener');
      expect(previews[0].textContent).toContain('in a new tab');
    });

    it('rebuilds the links from the layout choices', async () => {
      await create();

      const template = el().querySelector<HTMLSelectElement>('#publish-template')!;
      template.value = 'compact';
      template.dispatchEvent(new Event('change'));
      const units = el().querySelector<HTMLSelectElement>('#publish-units')!;
      units.value = 'metric';
      units.dispatchEvent(new Event('change'));
      const pageSize = el().querySelector<HTMLSelectElement>('#publish-page-size')!;
      pageSize.value = 'letter';
      pageSize.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      const pdf = links().find((a) => a.getAttribute('href')!.includes('/pdf'))!;
      expect(pdf.getAttribute('href')).toContain('t=compact');
      expect(pdf.getAttribute('href')).toContain('u=metric');
      expect(pdf.getAttribute('href')).toContain('p=letter');
    });

    it('contains no address other than the gateway export routes', async () => {
      await create();

      const html = el().innerHTML;
      expect(html).not.toMatch(/blob\.core|amazonaws|storage\.googleapis|workspaceId/i);
    });
  });

  describe('reloading', () => {
    it('reads again when the host reports a new saved version, and shows the newer one', async () => {
      await create(3);
      service.outcomes = [{ status: 'found', summary: summary({ versionNumber: 4 }) }];

      fixture.componentRef.setInput('versionHint', 4);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(service.calls).toBe(2);
      expect(text()).toContain('Version 4');
    });

    it('lets a creator check again on demand, and never lets a slow read overwrite a newer one', async () => {
      await create();
      const component = fixture.componentInstance;

      let releaseSlow!: (outcome: RecipeExportSummaryOutcome) => void;
      service.getSummary = () =>
        new Promise<RecipeExportSummaryOutcome>((resolve) => {
          releaseSlow = resolve;
        });
      const slow = component.load();

      service.getSummary = async () => ({ status: 'found', summary: summary({ versionNumber: 9 }) });
      await component.load();
      releaseSlow({ status: 'found', summary: summary({ versionNumber: 2 }) });
      await slow;
      fixture.detectChanges();

      expect(component.summary()?.versionNumber).toBe(9);
    });
  });

  describe('accessibility', () => {
    it('names every region by its heading', async () => {
      await create();

      for (const section of Array.from(el().querySelectorAll('section.block'))) {
        const labelledBy = section.getAttribute('aria-labelledby')!;
        expect(el().querySelector(`#${labelledBy}`)?.textContent?.trim().length).toBeGreaterThan(0);
      }
    });

    it('gives every select a programmatic label', async () => {
      await create();

      for (const id of ['publish-template', 'publish-units', 'publish-page-size']) {
        expect(el().querySelector(`label[for="${id}"]`)?.textContent?.trim().length).toBeGreaterThan(0);
      }
    });

    it('announces a finished read politely', async () => {
      await create();

      const live = el().querySelector('[aria-live="polite"]')!;
      expect(live.textContent).toContain('Export options loaded for version 3');
    });

    it('uses real lists for the copy rows and the downloads', async () => {
      await create();

      expect(el().querySelectorAll('ul.copy-list > li').length).toBe(2);
      expect(el().querySelectorAll('ul.download-list > li').length).toBe(3);
    });

    it('does no export work itself: it injects no HTTP client', () => {
      const source = RecipePublishPanelComponent.toString();

      expect(source).not.toContain('HttpClient');
    });
  });
});
