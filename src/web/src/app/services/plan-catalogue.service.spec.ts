import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { PlanCatalogueService } from './plan-catalogue.service';

const URL = 'https://gateway.example/api/v1/plans';

const rates = { textDraft: 10, longFormSection: 10 };

const plan = {
  key: 'creator',
  name: 'Creator',
  monthlyPrice: 19,
  annualMonthlyPrice: 15,
  annualPrice: 180,
  annualSaving: 48,
  credits: 6000,
  exampleMix: '50 recipe drafts',
  dailyImageCap: 25,
  qualityClasses: ['draft'],
  recommended: true,
  trialDays: null,
  trialImageCap: null,
  picturesAtDefaultQuality: 120,
};

describe('PlanCatalogueService', () => {
  let service: PlanCatalogueService;
  let http: HttpTestingController;

  async function configure(gatewayUrl: string | null): Promise<void> {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    const request = http.expectOne('/runtime-config.json');
    if (gatewayUrl === null) request.flush(null, { status: 503, statusText: 'unavailable' });
    else request.flush({ gatewayUrl });
    await loading;

    service = TestBed.inject(PlanCatalogueService);
  }

  afterEach(() => http.verify());

  it('reads the catalogue with a GET', async () => {
    await configure('https://gateway.example');
    const pending = firstValueFrom(service.catalogue());

    const request = http.expectOne(URL);
    expect(request.request.method).toBe('GET');
    // Anonymous: nothing about the caller is sent, so a shared cache can hold one answer for everyone.
    expect(request.request.withCredentials).toBeFalse();
    request.flush({ currency: 'USD', plans: [plan], packs: [], rates, pictureClasses: [], defaultPictureClass: null });

    const catalogue = await pending;
    expect(catalogue.plans[0].key).toBe('creator');
    expect(catalogue.plans[0].annualSaving).toBe(48);
  });

  /**
   * Every failure is the same answer — nothing to show — because the page's only question is whether there is
   * pricing. A marketing page showing an error about a catalogue would tell a visitor nothing they can use.
   */
  it('answers with an empty catalogue when the route fails', async () => {
    await configure('https://gateway.example');
    const pending = firstValueFrom(service.catalogue());

    http.expectOne(URL).flush(null, { status: 500, statusText: 'server error' });

    expect(await pending).toEqual(PlanCatalogueService.EMPTY);
  });

  it('answers with an empty catalogue when the body is not one', async () => {
    await configure('https://gateway.example');
    const pending = firstValueFrom(service.catalogue());

    http.expectOne(URL).flush({ unexpected: true });

    expect(await pending).toEqual(PlanCatalogueService.EMPTY);
  });

  it('asks for nothing while the gateway address is unknown', async () => {
    await configure(null);

    expect(await firstValueFrom(service.catalogue())).toEqual(PlanCatalogueService.EMPTY);
    http.expectNone(URL);
  });
});
