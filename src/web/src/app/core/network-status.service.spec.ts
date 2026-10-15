import { TestBed } from '@angular/core/testing';

import { NetworkStatusService } from './network-status.service';

describe('NetworkStatusService', () => {
  let onLine: jasmine.Spy<() => boolean>;

  beforeEach(() => {
    onLine = spyOnProperty(navigator, 'onLine').and.returnValue(true);
    TestBed.configureTestingModule({});
  });

  it('follows the browser going offline and coming back', () => {
    const network = TestBed.inject(NetworkStatusService);
    expect(network.online()).toBeTrue();

    onLine.and.returnValue(false);
    window.dispatchEvent(new Event('offline'));
    expect(network.online()).toBeFalse();

    onLine.and.returnValue(true);
    window.dispatchEvent(new Event('online'));
    expect(network.online()).toBeTrue();
  });

  it('calls back once when the browser comes back, and not after being cancelled', () => {
    const network = TestBed.inject(NetworkStatusService);
    const once = jasmine.createSpy('once');
    const cancelled = jasmine.createSpy('cancelled');

    network.whenOnline(once);
    network.whenOnline(cancelled)();
    window.dispatchEvent(new Event('online'));
    window.dispatchEvent(new Event('online'));

    expect(once).toHaveBeenCalledTimes(1);
    expect(cancelled).not.toHaveBeenCalled();
  });
});
