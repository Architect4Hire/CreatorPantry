import { TestBed } from '@angular/core/testing';

import { PrintService } from './print.service';

describe('PrintService', () => {
  it('opens the browser print dialog', () => {
    const print = spyOn(globalThis, 'print');

    TestBed.inject(PrintService).print();

    expect(print).toHaveBeenCalledTimes(1);
  });
});
