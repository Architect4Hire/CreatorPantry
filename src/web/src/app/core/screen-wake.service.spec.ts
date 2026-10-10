import { TestBed } from '@angular/core/testing';

import { ScreenWakeService } from './screen-wake.service';

/** A granted lock, standing in for one the test browser will not hand out to a window nobody is watching. */
class FakeSentinel {
  released = false;
  releases = 0;

  release(): Promise<void> {
    this.releases += 1;
    this.released = true;

    return Promise.resolve();
  }
}

function asSentinel(fake: FakeSentinel): WakeLockSentinel {
  return fake as unknown as WakeLockSentinel;
}

describe('ScreenWakeService', () => {
  let service: ScreenWakeService;

  beforeEach(() => {
    service = TestBed.inject(ScreenWakeService);
    // The window running these tests may well be in the background, and the service declines to ask then.
    spyOnProperty(Document.prototype, 'visibilityState', 'get').and.returnValue('visible');
  });

  afterEach(() => {
    // Leaves no listener on the shared document behind for the next test.
    service.release();
  });

  it('asks the browser to keep the screen on', async () => {
    const request = spyOn(navigator.wakeLock, 'request').and.resolveTo(asSentinel(new FakeSentinel()));

    await service.hold();

    expect(request).toHaveBeenCalledOnceWith('screen');
    expect(service.isRefused()).toBeFalse();
  });

  it('reports a refusal instead of throwing', async () => {
    spyOn(navigator.wakeLock, 'request').and.rejectWith(new DOMException('battery', 'NotAllowedError'));

    await service.hold();

    expect(service.isRefused()).toBeTrue();
  });

  it('reports a refusal when the browser cannot do it at all', async () => {
    spyOnProperty(Navigator.prototype, 'wakeLock', 'get').and.returnValue(undefined as unknown as WakeLock);

    expect(service.isSupported).toBeFalse();

    await service.hold();

    expect(service.isRefused()).toBeTrue();
  });

  it('asks again when the page comes back into view, the browser having taken the lock back', async () => {
    const taken = new FakeSentinel();
    const request = spyOn(navigator.wakeLock, 'request').and.resolveTo(asSentinel(taken));
    await service.hold();

    // What the browser does to every wake lock the moment its page is hidden.
    taken.released = true;
    request.and.resolveTo(asSentinel(new FakeSentinel()));
    document.dispatchEvent(new Event('visibilitychange'));
    await Promise.resolve();

    expect(request).toHaveBeenCalledTimes(2);
    expect(service.isRefused()).toBeFalse();
  });

  it('does not ask a second time while the lock is still held', async () => {
    const request = spyOn(navigator.wakeLock, 'request').and.resolveTo(asSentinel(new FakeSentinel()));
    await service.hold();

    document.dispatchEvent(new Event('visibilitychange'));
    await service.hold();

    expect(request).toHaveBeenCalledTimes(1);
  });

  it('lets the screen go, and stops asking for it', async () => {
    const held = new FakeSentinel();
    const request = spyOn(navigator.wakeLock, 'request').and.resolveTo(asSentinel(held));
    await service.hold();

    service.release();

    expect(held.releases).toBe(1);

    document.dispatchEvent(new Event('visibilitychange'));
    await Promise.resolve();

    expect(request).toHaveBeenCalledTimes(1);
  });

  it('forgets a refusal once the screen is let go', async () => {
    spyOn(navigator.wakeLock, 'request').and.rejectWith(new DOMException('battery', 'NotAllowedError'));
    await service.hold();

    service.release();

    expect(service.isRefused()).toBeFalse();
  });

  it('drops a lock that arrives after it was let go', async () => {
    const late = new FakeSentinel();
    spyOn(navigator.wakeLock, 'request').and.returnValue(
      new Promise<WakeLockSentinel>((resolve) => setTimeout(() => resolve(asSentinel(late)), 0)),
    );

    const holding = service.hold();
    service.release();
    await holding;

    expect(late.releases).toBe(1);
  });

  it('does nothing when let go having never held anything', () => {
    const request = spyOn(navigator.wakeLock, 'request');

    service.release();

    expect(request).not.toHaveBeenCalled();
  });
});
