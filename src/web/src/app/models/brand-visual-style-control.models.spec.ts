import { BrandGuideChoice } from './brand-guide-control.models';
import {
  BRAND_VISUAL_MAX_REFERENCES,
  BrandVisualReference,
  REFERENCE_UNUSABLE_FALLBACK,
  resolveBrandVisualConflicts,
  resolveBrandVisualUse,
} from './brand-visual-style-control.models';

const active: BrandGuideChoice = { guideId: 'g1', name: 'A', versionNumber: 3, isStale: false };
const other: BrandGuideChoice = { guideId: 'g2', name: 'B', versionNumber: 1, isStale: false };
const stale: BrandGuideChoice = { guideId: 'g3', name: 'C', versionNumber: 2, isStale: true };

const usable: BrandVisualReference = { documentId: 'd1', title: 'Moodboard', usable: true };
const unusable: BrandVisualReference = { documentId: 'd2', title: 'Scan', usable: false };

describe('resolveBrandVisualUse', () => {
  const base = { activeGuide: active, choices: [other, stale], staleAcknowledged: false, hasVisualGuidance: true };

  it('uses the active guide by default', () => {
    expect(resolveBrandVisualUse({ ...base, selection: { kind: 'default' } })).toEqual({
      kind: 'guide',
      source: 'default',
      guide: active,
      stale: false,
      blocked: false,
    });
  });

  it('tells "no visual guidance" apart from "no guide", and does not block', () => {
    const noLook = resolveBrandVisualUse({ ...base, hasVisualGuidance: false, selection: { kind: 'default' } });
    const noGuide = resolveBrandVisualUse({ ...base, activeGuide: null, selection: { kind: 'default' } });

    expect(noLook).toEqual(jasmine.objectContaining({ kind: 'no-visual-guide', blocked: false }));
    expect(noGuide).toEqual({ kind: 'none', reason: 'no-active-guide', blocked: false });
  });

  it('keeps a stale guide blocked until acknowledged, even with no visual guidance', () => {
    const selection = { kind: 'override', guideId: 'g3', versionNumber: 2 } as const;

    expect(resolveBrandVisualUse({ ...base, selection })).toEqual(jasmine.objectContaining({ blocked: true }));
    expect(resolveBrandVisualUse({ ...base, selection, staleAcknowledged: true })).toEqual(
      jasmine.objectContaining({ blocked: false, stale: true }),
    );
    expect(resolveBrandVisualUse({ ...base, hasVisualGuidance: false, selection })).toEqual(
      jasmine.objectContaining({ kind: 'no-visual-guide', blocked: true }),
    );
  });

  it('never falls back when a chosen guide has gone', () => {
    expect(resolveBrandVisualUse({ ...base, selection: { kind: 'override', guideId: 'x', versionNumber: 9 } })).toEqual({
      kind: 'unavailable',
      blocked: true,
    });
  });

  it('honours the no-visual-style choice', () => {
    expect(resolveBrandVisualUse({ ...base, selection: { kind: 'none' } })).toEqual({
      kind: 'none',
      reason: 'chosen',
      blocked: false,
    });
  });
});

describe('resolveBrandVisualConflicts', () => {
  const guideUse = resolveBrandVisualUse({
    selection: { kind: 'default' },
    activeGuide: active,
    choices: [],
    staleAcknowledged: false,
    hasVisualGuidance: true,
  });

  it('reports nothing when every chosen reference is usable', () => {
    expect(
      resolveBrandVisualConflicts({ use: guideUse, activeGuide: active, references: [usable], selectedReferenceIds: ['d1'] }),
    ).toEqual([]);
  });

  it('reports an unreadable reference before submit, with a reason that says the image is never sent', () => {
    const [conflict] = resolveBrandVisualConflicts({
      use: guideUse,
      activeGuide: active,
      references: [usable, unusable],
      selectedReferenceIds: ['d2'],
    });

    expect(conflict).toEqual({ kind: 'reference-unusable', documentId: 'd2', title: 'Scan', reason: REFERENCE_UNUSABLE_FALLBACK });
    expect(REFERENCE_UNUSABLE_FALLBACK).toContain('image is never sent');
  });

  it('prefers a supplied reason', () => {
    const [conflict] = resolveBrandVisualConflicts({
      use: guideUse,
      activeGuide: active,
      references: [{ ...unusable, unusableReason: 'Still being read.' }],
      selectedReferenceIds: ['d2'],
    });

    expect(conflict).toEqual(jasmine.objectContaining({ reason: 'Still being read.' }));
  });

  it('reports a selected reference that is no longer in the library', () => {
    expect(
      resolveBrandVisualConflicts({ use: guideUse, activeGuide: active, references: [], selectedReferenceIds: ['gone'] }),
    ).toEqual([{ kind: 'reference-missing', documentId: 'gone' }]);
  });

  it('reports an override that is not the active guide', () => {
    const use = resolveBrandVisualUse({
      selection: { kind: 'override', guideId: 'g2', versionNumber: 1 },
      activeGuide: active,
      choices: [other],
      staleAcknowledged: false,
      hasVisualGuidance: true,
    });

    expect(resolveBrandVisualConflicts({ use, activeGuide: active, references: [], selectedReferenceIds: [] })).toEqual([
      { kind: 'guide-not-active', name: 'B', versionNumber: 1 },
    ]);
  });

  it('stays silent about references when no guide is in use, because none would be sent', () => {
    const use = resolveBrandVisualUse({
      selection: { kind: 'none' },
      activeGuide: active,
      choices: [],
      staleAcknowledged: false,
      hasVisualGuidance: true,
    });

    expect(
      resolveBrandVisualConflicts({ use, activeGuide: active, references: [unusable], selectedReferenceIds: ['d2'] }),
    ).toEqual([]);
  });

  it('shares the server cap on reference documents', () => {
    expect(BRAND_VISUAL_MAX_REFERENCES).toBe(10);
  });
});
