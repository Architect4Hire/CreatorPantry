import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { isPathUnder } from './form-issue';
import { maxLength, required, warn } from './form-rules';
import { FormValidationService, FormValidator } from './form-validator';

interface Model {
  title: string;
  lines: { quantity: string }[];
}

const RULES = {
  title: [required('Name it.'), warn(maxLength(5, 'A long title.'))],
  'lines[].quantity': [required('How much?')],
};

function validator(initial: Model = { title: 'Soup', lines: [] }) {
  const model = signal(initial);

  return { model, validator: new FormValidator<Model>(RULES, model) };
}

describe('FormValidator', () => {
  it('does not show a client error before the creator has tried to save or left the field', () => {
    const { validator: v } = validator({ title: '', lines: [] });

    expect(v.issues().length).toBe(1);
    expect(v.firstError('title')).toBe('');
    expect(v.visibleErrors()).toEqual([]);
  });

  it('shows a client error once the field has been touched', () => {
    const { validator: v } = validator({ title: '', lines: [] });

    v.touch('title');

    expect(v.firstError('title')).toBe('Name it.');
  });

  it('shows every client error after an attempt to save', () => {
    const { validator: v } = validator({ title: '', lines: [{ quantity: '' }] });

    v.attempt();

    expect(v.visibleErrors().map((issue) => issue.path)).toEqual(['title', 'lines[0].quantity']);
  });

  it('shows a warning as soon as it is true, without waiting for an attempt', () => {
    const { validator: v } = validator({ title: 'A rather long title', lines: [] });

    expect(v.firstWarning('title')).toBe('A long title.');
    expect(v.visibleWarnings().length).toBe(1);
  });

  it('follows the model as it changes', () => {
    const { model, validator: v } = validator({ title: '', lines: [] });
    v.attempt();
    expect(v.firstError('title')).toBe('Name it.');

    model.set({ title: 'Soup', lines: [] });

    expect(v.firstError('title')).toBe('');
  });

  it('blocks a save for a client error even before it is shown, but never for a warning', () => {
    const blocked = validator({ title: '', lines: [] }).validator;
    const warned = validator({ title: 'A rather long title', lines: [] }).validator;

    expect(blocked.canSubmit()).toBeFalse();
    expect(warned.canSubmit()).toBeTrue();
  });

  it('shows the server refusal at once and does not let it block a resubmit', () => {
    const { validator: v } = validator();

    v.applyServerErrors({ title: ['Taken.'], 'lines[0].quantity': ['Not a quantity.'] });

    expect(v.firstError('title')).toBe('Taken.');
    expect(v.issuesFor('lines[0].quantity')[0]?.source).toBe('server');
    expect(v.canSubmit()).toBeTrue();
  });

  it('replaces the previous refusal with the new answer', () => {
    const { validator: v } = validator();
    v.applyServerErrors({ title: ['Taken.'] });

    v.applyServerErrors({ 'lines[0].quantity': ['Bad.'] });

    expect(v.firstError('title')).toBe('');
    expect(v.firstError('lines[0].quantity')).toBe('Bad.');
  });

  it('counts a refusal as an attempt so the client errors it did not mention show too', () => {
    const { validator: v } = validator({ title: '', lines: [] });

    v.applyServerErrors({ other: ['Nope.'] });

    expect(v.firstError('title')).toBe('Name it.');
  });

  it('clears the server word about a field and what is inside it, and only that', () => {
    const { validator: v } = validator();
    v.applyServerErrors({ title: ['Taken.'], 'lines[0].quantity': ['Bad.'], 'lines[1].quantity': ['Bad.'], servings: ['Odd.'] });

    v.clearServer('lines[0]');
    expect(v.issuesFor('lines[0].quantity')).toEqual([]);
    expect(v.firstError('lines[1].quantity')).toBe('Bad.');

    v.clearServer('title');
    expect(v.firstError('title')).toBe('');
    expect(v.firstError('servings')).toBe('Odd.');

    v.clearServer();
    expect(v.firstError('servings')).toBe('');
  });

  it('reads a server record the form owns, and follows the form writing to it directly', () => {
    const owned = signal<Readonly<Record<string, readonly string[]>>>({});
    const v = new FormValidator<Model>(RULES, signal<Model>({ title: 'Soup', lines: [] }), owned);

    owned.set({ title: ['Taken.'] });
    expect(v.firstError('title')).toBe('Taken.');

    v.clearServer('title');
    expect(owned()).toEqual({});

    owned.set({ title: ['Taken.'] });
    v.reset();
    expect(owned()).toEqual({});
  });

  it('counts visible issues under a prefix for a tab or section', () => {
    const { validator: v } = validator({ title: '', lines: [{ quantity: '' }, { quantity: '2' }, { quantity: '' }] });
    v.attempt();
    v.applyServerErrors({ 'lines[0].quantity': ['Bad.'] });

    expect(v.countUnder('lines')).toBe(3);
    expect(v.countUnder('title')).toBe(1);
    expect(v.countUnder('title', 'warning')).toBe(0);
  });

  it('returns to a form nobody has touched on reset', () => {
    const { validator: v } = validator({ title: '', lines: [] });
    v.attempt();
    v.touch('title');
    v.applyServerErrors({ title: ['Taken.'] });

    v.reset();

    expect(v.visibleIssues()).toEqual([]);
  });
});

describe('FormValidationService', () => {
  it('makes a validator that reads the model it was given', () => {
    const model = signal<Model>({ title: '', lines: [] });
    const made = TestBed.inject(FormValidationService).create<Model>(RULES, model);

    expect(made.canSubmit()).toBeFalse();
  });
});

describe('isPathUnder', () => {
  it('matches the path itself, a child and an indexed row, and not a sibling that shares a prefix', () => {
    expect(isPathUnder('lines', 'lines')).toBeTrue();
    expect(isPathUnder('lines[0].quantity', 'lines')).toBeTrue();
    expect(isPathUnder('lines.total', 'lines')).toBeTrue();
    expect(isPathUnder('linesExtra', 'lines')).toBeFalse();
    expect(isPathUnder('lines', 'lines[0]')).toBeFalse();
  });
});
