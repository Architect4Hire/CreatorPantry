import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  BrandStyleGuideComparisonState,
  BrandStyleGuideRuleComparison,
  BrandStyleGuideSectionComparison,
  BrandStyleGuideSourceComparison,
  BrandStyleGuideVersionComparison,
} from '../../models/brand-style-guide.models';
import { BrandGuideVersionComparisonComponent } from './brand-guide-version-comparison.component';

function section(
  sectionKey: string,
  state: BrandStyleGuideComparisonState,
  overrides: Partial<BrandStyleGuideSectionComparison> = {},
): BrandStyleGuideSectionComparison {
  return { sectionKey, channelKey: null, state, fromBody: null, toBody: null, ...overrides };
}

function rule(
  text: string,
  state: BrandStyleGuideComparisonState,
  overrides: Partial<BrandStyleGuideRuleComparison> = {},
): BrandStyleGuideRuleComparison {
  return { kind: 'Do', text, state, fromRank: null, toRank: null, ...overrides };
}

function source(
  documentId: string,
  state: BrandStyleGuideComparisonState,
  overrides: Partial<BrandStyleGuideSourceComparison> = {},
): BrandStyleGuideSourceComparison {
  return { documentId, state, fromVersionNumber: null, toVersionNumber: null, ...overrides };
}

function comparison(
  overrides: Partial<BrandStyleGuideVersionComparison> = {},
): BrandStyleGuideVersionComparison {
  return {
    from: { versionId: 'v3', versionNumber: 3, status: 'Approved', createdAt: '2026-09-01T00:00:00Z' },
    to: { versionId: 'v5', versionNumber: 5, status: 'Draft', createdAt: '2026-09-20T00:00:00Z' },
    sections: [],
    rules: [],
    sources: [],
    hasChanges: true,
    ...overrides,
  };
}

describe('BrandGuideVersionComparisonComponent', () => {
  let fixture: ComponentFixture<BrandGuideVersionComparisonComponent>;

  function render(value: BrandStyleGuideVersionComparison): void {
    fixture = TestBed.createComponent(BrandGuideVersionComparisonComponent);
    fixture.componentRef.setInput('comparison', value);
    fixture.detectChanges();
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function rows(): HTMLElement[] {
    return Array.from(root().querySelectorAll<HTMLElement>('.row'));
  }

  function legendLabels(): string[] {
    return Array.from(root().querySelectorAll<HTMLElement>('cp-diff-legend .label')).map(
      (each) => each.textContent?.trim() ?? '',
    );
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [BrandGuideVersionComparisonComponent] });
  });

  // ---- What it says about the two versions ----

  it('names both versions in words rather than with an arrow alone', () => {
    render(comparison({ sections: [section('Voice', 'Changed', { fromBody: 'Plain.', toBody: 'Warm.' })] }));

    const heading = root().querySelector('h3');

    expect(heading?.textContent).toContain('Version 3 compared with version 5');
    expect(root().querySelector('section.comparison')?.getAttribute('aria-labelledby')).toBe(heading?.id);
  });

  it('says the two versions are identical when nothing changed, and lists no rows', () => {
    render(
      comparison({
        hasChanges: false,
        sections: [section('Voice', 'Unchanged'), section('Tone', 'Unchanged')],
        rules: [rule('Say it plainly.', 'Unchanged')],
      }),
    );

    expect(text()).toContain('say exactly the same thing');
    expect(rows()).toHaveSize(0);
    // Each part still accounts for what the versions hold, so "identical" describes two real versions.
    expect(text()).toContain('2 unchanged');
    expect(text()).toContain('1 unchanged');
  });

  it('renders no legend when there is nothing to explain', () => {
    render(comparison({ hasChanges: false, sections: [section('Voice', 'Unchanged')] }));

    expect(root().querySelector('cp-diff-legend')).toBeNull();
  });

  // ---- Sections ----

  it('labels a section with the creator-facing name for its key, not the enum name', () => {
    render(comparison({ sections: [section('WritingStyle', 'Changed', { fromBody: 'a', toBody: 'b' })] }));

    expect(text()).toContain('Writing style');
    expect(text()).not.toContain('WritingStyle');
  });

  it('names the channel on a channel variant, which is the one key held once per channel', () => {
    render(
      comparison({
        sections: [section('ChannelVariant', 'Added', { channelKey: 'instagram', toBody: 'Short.' })],
      }),
    );

    expect(text()).toContain('Channel: instagram');
  });

  it('shows both bodies of a changed section, the old one marked as what it was', () => {
    render(comparison({ sections: [section('Voice', 'Changed', { fromBody: 'Plain.', toBody: 'Warm.' })] }));

    expect(text()).toContain('was');
    expect(text()).toContain('Plain.');
    expect(text()).toContain('now');
    expect(text()).toContain('Warm.');
  });

  /**
   * A section key this build has no words for must still render. Dropping the row would hide a change, which
   * is the one thing this screen exists to prevent.
   */
  it('renders a section key it has no label for, spaced out from the identifier', () => {
    render(comparison({ sections: [section('SomeFutureKey', 'Added', { toBody: 'New.' })] }));

    expect(rows()).toHaveSize(1);
    expect(text()).toContain('Some future key');
  });

  // ---- Rules ----

  it('labels a rule by its kind and its own words', () => {
    render(comparison({ rules: [rule('Name the pan size', 'Added', { kind: 'Dont' })] }));

    expect(text()).toContain('Never: Name the pan size');
  });

  it('says where a moved rule went, as positions rather than ranks', () => {
    render(comparison({ rules: [rule('Say it plainly.', 'Moved', { fromRank: 0, toRank: 2 })] }));

    expect(text()).toContain('Position 1 → 3');
  });

  it('shows no before-and-after values for a rule, which is never reported as changed', () => {
    render(comparison({ rules: [rule('Say it plainly.', 'Removed', { fromRank: 0 })] }));

    expect(root().querySelector('.row-values')).toBeNull();
  });

  // ---- Cited examples ----

  it('shows a re-pinned citation as one change naming both document versions', () => {
    render(comparison({ sources: [source('d1', 'Changed', { fromVersionNumber: 1, toVersionNumber: 2 })] }));

    expect(text()).toContain('version 1');
    expect(text()).toContain('version 2');
    expect(rows()).toHaveSize(1);
  });

  // ---- Kinds, glyphs and the legend ----

  it('marks each row with the state the server sent and says that state in words', () => {
    render(
      comparison({
        sections: [
          section('Voice', 'Added', { toBody: 'a' }),
          section('Tone', 'Removed', { fromBody: 'b' }),
          section('Audience', 'Changed', { fromBody: 'c', toBody: 'd' }),
        ],
        rules: [rule('Say it plainly.', 'Moved', { fromRank: 1, toRank: 0 })],
      }),
    );

    expect(rows().map((row) => row.dataset['kind'])).toEqual(['added', 'removed', 'changed', 'moved']);
    expect(text()).toContain('Added');
    expect(text()).toContain('Removed');
    expect(text()).toContain('Changed');
    expect(text()).toContain('Moved');
  });

  it('explains only the kinds actually on screen', () => {
    render(
      comparison({
        sections: [section('Voice', 'Added', { toBody: 'a' }), section('Tone', 'Unchanged')],
      }),
    );

    expect(legendLabels()).toEqual(['Added', 'Unchanged']);
  });

  /**
   * `BrandStyleGuideComparisonState` is documented as an enum that may grow. A state this build cannot name
   * is shown as a change that needs attention — never as "unchanged", which would be a lie.
   */
  it('shows a state it does not recognise as a change it cannot describe', () => {
    render(comparison({ sections: [section('Voice', 'Unknown', { fromBody: 'a', toBody: 'b' })] }));

    expect(rows()[0].dataset['kind']).toBe('warning');
    expect(text()).toContain('cannot say how');
  });

  // ---- Accessibility ----

  it('states each row kind as text beside the decorative glyph', () => {
    render(comparison({ sections: [section('Voice', 'Added', { toBody: 'a' })] }));

    const glyph = root().querySelector('.row-glyph');
    const kind = root().querySelector('.row-kind');

    expect(glyph?.getAttribute('aria-hidden')).toBe('true');
    expect(kind?.textContent?.trim()).toBe('Added');
  });

  it('gives each mounted panel its own heading id', () => {
    render(comparison({ sections: [section('Voice', 'Added', { toBody: 'a' })] }));
    const first = root().querySelector('h3')?.id;

    render(comparison({ sections: [section('Voice', 'Added', { toBody: 'a' })] }));
    const second = root().querySelector('h3')?.id;

    expect(first).toBeTruthy();
    expect(second).not.toBe(first);
  });

  it('says every part is empty when neither version holds anything', () => {
    render(comparison({ hasChanges: false }));

    expect(text()).toContain('Neither version has any sections.');
    expect(text()).toContain("Neither version has any do or don't rules.");
    expect(text()).toContain('Neither version cites any of your examples.');
  });
});
