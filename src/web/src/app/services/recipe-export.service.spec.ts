import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DEFAULT_EXPORT_CHOICES, RecipeExportChoices } from '../models/recipe-export.models';
import { RecipeExportService } from './recipe-export.service';

const SUMMARY_URL = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/exports/summary';

const SUMMARY = {
  versionNumber: 2,
  exportable: true,
  notExportableReason: null,
  editorial: null,
  seo: null,
};

describe('RecipeExportService', () => {
  let service: RecipeExportService;
  let http: HttpTestingController;

  async function configure(ready: boolean): Promise<void> {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    if (ready) {
      const loading = TestBed.inject(RuntimeConfigService).load();
      http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
      await loading;
    }

    service = TestBed.inject(RecipeExportService);
  }

  afterEach(() => http.verify());

  describe('getSummary', () => {
    beforeEach(async () => configure(true));

    it('reads the workspace-scoped summary with credentials', async () => {
      const pending = service.getSummary('cozy-fall', 'r1');

      const request = http.expectOne(SUMMARY_URL);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      request.flush(SUMMARY);

      expect(await pending).toEqual({ status: 'found', summary: { ...SUMMARY } });
    });

    it('is unavailable, not found-by-guess, when the body is not the summary shape', async () => {
      const pending = service.getSummary('cozy-fall', 'r1');
      http.expectOne(SUMMARY_URL).flush({ versionNumber: 2, exportable: 'yes' });

      expect(await pending).toEqual({ status: 'unavailable' });
    });

    it('reports a 404 as not found, and every other failure as unavailable', async () => {
      const missing = service.getSummary('cozy-fall', 'r1');
      http.expectOne(SUMMARY_URL).flush({}, { status: 404, statusText: 'Not Found' });
      expect(await missing).toEqual({ status: 'not_found' });

      const broken = service.getSummary('cozy-fall', 'r1');
      http.expectOne(SUMMARY_URL).flush({}, { status: 500, statusText: 'Server Error' });
      expect(await broken).toEqual({ status: 'unavailable' });
    });

    it('encodes the route segments it is given', async () => {
      const pending = service.getSummary('a b', 'r/1');
      http.expectOne('https://gateway.example/api/v1/workspaces/a%20b/recipes/r%2F1/exports/summary').flush(SUMMARY);

      await pending;
    });
  });

  describe('downloadUrl', () => {
    beforeEach(async () => configure(true));

    const choices: RecipeExportChoices = { template: 'compact', units: 'metric', pageSize: 'letter' };

    it('pins the version and sends layout choices only to the routes that read them', () => {
      expect(service.downloadUrl('cozy-fall', 'r1', 'json-ld', 4, choices)).toBe(
        'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/exports/json-ld?versionNumber=4',
      );
      expect(service.downloadUrl('cozy-fall', 'r1', 'markdown', 4, choices)).toBe(
        'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/exports/markdown?versionNumber=4&template=compact&units=metric',
      );
      expect(service.downloadUrl('cozy-fall', 'r1', 'pdf', 4, choices)).toBe(
        'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/exports/pdf?versionNumber=4&template=compact&units=metric&pageSize=letter',
      );
    });

    it('only ever builds the gateway export route, with no workspace id or storage address in it', () => {
      for (const format of ['json-ld', 'markdown', 'pdf'] as const) {
        const url = new URL(service.downloadUrl('cozy-fall', 'r1', format, 1, DEFAULT_EXPORT_CHOICES)!);

        expect(url.origin).toBe('https://gateway.example');
        expect(url.pathname).toBe(`/api/v1/workspaces/cozy-fall/recipes/r1/exports/${format}`);
        expect([...url.searchParams.keys()]).not.toContain('workspaceId');
      }
    });
  });

  describe('before the gateway address is known', () => {
    beforeEach(async () => configure(false));

    it('is unavailable and builds no link, without making a request', async () => {
      expect(await service.getSummary('cozy-fall', 'r1')).toEqual({ status: 'unavailable' });
      expect(service.downloadUrl('cozy-fall', 'r1', 'pdf', 1, DEFAULT_EXPORT_CHOICES)).toBeNull();
    });
  });
});
