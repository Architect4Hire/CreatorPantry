import { TestBed } from '@angular/core/testing';

import { WorkTask } from '../../models/work-task.models';
import { QuickCaptureService } from './quick-capture.service';

describe('QuickCaptureService', () => {
  let service: QuickCaptureService;

  beforeEach(() => {
    service = TestBed.inject(QuickCaptureService);
  });

  it('opens the dialog the shell registered, with the page’s options, and nothing once it is gone', () => {
    const opener = jasmine.createSpy('opener');

    expect(service.available).toBeFalse();
    service.open({ today: '2038-10-14' });

    const unregister = service.register(opener);
    expect(service.available).toBeTrue();
    service.open({ today: '2038-10-14' });
    expect(opener).toHaveBeenCalledOnceWith({ today: '2038-10-14' });

    unregister();
    service.open();
    expect(opener).toHaveBeenCalledTimes(1);
    expect(service.available).toBeFalse();
  });

  it('leaves a newer registration in place when an older one is removed', () => {
    const first = jasmine.createSpy('first');
    const second = jasmine.createSpy('second');

    const removeFirst = service.register(first);
    service.register(second);
    removeFirst();
    service.open();

    expect(second).toHaveBeenCalled();
    expect(first).not.toHaveBeenCalled();
  });

  it('tells every listener about each captured task', () => {
    const heard: string[] = [];
    service.captured$.subscribe((task) => heard.push(task.id));

    service.notifyCaptured({ id: 't-1' } as WorkTask);
    service.notifyCaptured({ id: 't-2' } as WorkTask);

    expect(heard).toEqual(['t-1', 't-2']);
  });
});
