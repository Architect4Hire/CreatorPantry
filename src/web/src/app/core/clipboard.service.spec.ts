import { TestBed } from '@angular/core/testing';

import { ClipboardService } from './clipboard.service';

describe('ClipboardService', () => {
  let service: ClipboardService;

  beforeEach(() => {
    service = TestBed.inject(ClipboardService);
  });

  it('writes the text and reports that it did', async () => {
    const write = spyOn(navigator.clipboard, 'writeText').and.resolveTo();

    expect(await service.copy('A tight crop.')).toBeTrue();
    expect(write).toHaveBeenCalledOnceWith('A tight crop.');
  });

  it('reports a refused write instead of throwing', async () => {
    spyOn(navigator.clipboard, 'writeText').and.rejectWith(new DOMException('denied', 'NotAllowedError'));

    expect(await service.copy('A tight crop.')).toBeFalse();
  });
});
