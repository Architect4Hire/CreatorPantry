import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RuntimeConfigService, decodeRuntimeConfig } from './runtime-config.service';

describe('decodeRuntimeConfig', () => {
  it('accepts an absolute https gateway URL', () => {
    expect(decodeRuntimeConfig({ gatewayUrl: 'https://gateway.example' })).toEqual({
      gatewayUrl: 'https://gateway.example',
    });
  });

  it('rejects missing, relative, and non-http values', () => {
    for (const value of [null, 'x', {}, { gatewayUrl: 42 }, { gatewayUrl: '/relative' }, { gatewayUrl: 'javascript:alert(1)' }]) {
      expect(decodeRuntimeConfig(value)).withContext(JSON.stringify(value)).toBeNull();
    }
  });
});

describe('RuntimeConfigService', () => {
  let service: RuntimeConfigService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(RuntimeConfigService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('starts in the loading state', () => {
    expect(service.state()).toEqual({ status: 'loading' });
  });

  it('becomes ready with a valid configuration', async () => {
    const loading = service.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    expect(service.state()).toEqual({ status: 'ready', config: { gatewayUrl: 'https://gateway.example' } });
  });

  it('degrades to the error state when the response is invalid', async () => {
    const loading = service.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: '' });
    await loading;

    expect(service.state()).toEqual({ status: 'error' });
  });

  it('degrades to the error state when the request fails', async () => {
    const loading = service.load();
    http.expectOne('/runtime-config.json').flush('unavailable', { status: 503, statusText: 'Service Unavailable' });
    await loading;

    expect(service.state()).toEqual({ status: 'error' });
  });
});
