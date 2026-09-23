import { TestBed } from '@angular/core/testing';

import { CpDiffKind, CpDiffLegendComponent } from './diff-legend.component';

describe('CpDiffLegendComponent', () => {
  const canonicalOrder: CpDiffKind[] = ['added', 'removed', 'changed', 'moved', 'unchanged', 'warning', 'selected'];
  const defaultLabels: Record<CpDiffKind, string> = {
    added: 'Added',
    removed: 'Removed',
    changed: 'Changed',
    moved: 'Moved',
    unchanged: 'Unchanged',
    warning: 'Needs attention',
    selected: 'Selected',
  };
  const defaultGlyphs: Record<CpDiffKind, string> = {
    added: '+',
    removed: '−',
    changed: '~',
    moved: '↕',
    unchanged: '·',
    warning: '!',
    selected: '✓',
  };

  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [CpDiffLegendComponent] }).compileComponents();
    return TestBed.createComponent(CpDiffLegendComponent);
  }

  it('renders all 7 kinds in the canonical order with their default labels by default', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const entries = Array.from(fixture.nativeElement.querySelectorAll('.entry')) as HTMLElement[];
    expect(entries.length).toBe(7);
    entries.forEach((entry, index) => {
      const kind = canonicalOrder[index];
      expect(entry.getAttribute('data-kind')).toBe(kind);
      expect(entry.querySelector('.label')?.textContent).toBe(defaultLabels[kind]);
      expect(entry.querySelector('.swatch')?.textContent).toBe(defaultGlyphs[kind]);
    });
  });

  it('renders only the given subset in the given order when show is set', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('show', ['warning', 'added'] as CpDiffKind[]);
    fixture.detectChanges();

    const entries = Array.from(fixture.nativeElement.querySelectorAll('.entry')) as HTMLElement[];
    expect(entries.length).toBe(2);
    expect(entries[0].getAttribute('data-kind')).toBe('warning');
    expect(entries[1].getAttribute('data-kind')).toBe('added');
  });

  it('overrides specific label text via labels input while the glyph stays the default', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('labels', { warning: 'Custom warning text' });
    fixture.detectChanges();

    const entries = Array.from(fixture.nativeElement.querySelectorAll('.entry')) as HTMLElement[];
    const warningEntry = entries.find((entry) => entry.getAttribute('data-kind') === 'warning')!;
    const addedEntry = entries.find((entry) => entry.getAttribute('data-kind') === 'added')!;

    expect(warningEntry.querySelector('.label')?.textContent).toBe('Custom warning text');
    expect(warningEntry.querySelector('.swatch')?.textContent).toBe(defaultGlyphs['warning']);
    expect(addedEntry.querySelector('.label')?.textContent).toBe(defaultLabels['added']);
  });

  it('marks every swatch aria-hidden and keeps every label as real visible text content', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const entries = Array.from(fixture.nativeElement.querySelectorAll('.entry')) as HTMLElement[];
    expect(entries.length).toBe(7);
    for (const entry of entries) {
      const swatch = entry.querySelector('.swatch') as HTMLElement;
      const label = entry.querySelector('.label') as HTMLElement;
      expect(swatch.getAttribute('aria-hidden')).toBe('true');
      expect(label.hasAttribute('aria-hidden')).toBe(false);
      expect(label.textContent?.trim().length).toBeGreaterThan(0);
    }
  });

  it('exposes the list as an explicit role="list" with one role-less li per entry', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const list = fixture.nativeElement.querySelector('ul.legend') as HTMLElement;
    expect(list.getAttribute('role')).toBe('list');
    expect(list.querySelectorAll('li.entry').length).toBe(7);
  });
});
