import { custom, FormRules, inform, isWebAddress, maxLength, minLength, pattern, range, required, warn, webAddress } from './form-rules';
import { validateModel } from './validate-model';

interface Line {
  quantity: string;
  note: string;
}

interface Model {
  title: string;
  servings: number | null;
  lines: Line[];
}

function model(overrides: Partial<Model> = {}): Model {
  return { title: 'Soup', servings: 4, lines: [], ...overrides };
}

describe('validateModel', () => {
  it('returns nothing when every rule passes', () => {
    const rules: FormRules<Model> = { title: [required('Name it.'), maxLength(10, 'Too long.')] };

    expect(validateModel(model(), rules)).toEqual([]);
  });

  it('reports a failure with its path, code, severity and source', () => {
    const rules: FormRules<Model> = { title: [required('Name it.')] };

    expect(validateModel(model({ title: '   ' }), rules)).toEqual([
      { path: 'title', severity: 'error', code: 'required', message: 'Name it.', source: 'client' },
    ]);
  });

  it('expands [] to a concrete path for each row that exists', () => {
    const rules: FormRules<Model> = { 'lines[].quantity': [required('How much?')] };
    const lines = [
      { quantity: '1', note: '' },
      { quantity: '', note: '' },
      { quantity: ' ', note: '' },
    ];

    expect(validateModel(model({ lines }), rules).map((issue) => issue.path)).toEqual(['lines[1].quantity', 'lines[2].quantity']);
  });

  it('finds no rows to check when the list is empty or is not a list', () => {
    const rules: FormRules<Model> = { 'lines[].quantity': [required('How much?')] };

    expect(validateModel(model({ lines: [] }), rules)).toEqual([]);
    expect(validateModel({ lines: 'nope' } as unknown as Model, rules)).toEqual([]);
  });

  it('hands a missing value to the rule rather than skipping it', () => {
    const rules: FormRules<Model> = { servings: [required('How many does it serve?')] };

    expect(validateModel(model({ servings: null }), rules).map((issue) => issue.code)).toEqual(['required']);
  });

  it('lets a rule compare a field with another through the model', () => {
    const rules: FormRules<Model> = {
      note: [],
      servings: [custom<Model>('tooMany', (value, { model: whole }) => (Number(value) > whole.lines.length * 10 ? 'More than the lines suggest.' : null))],
    };

    expect(validateModel(model({ servings: 50 }), rules).map((issue) => issue.code)).toEqual(['tooMany']);
    expect(validateModel(model({ servings: 5, lines: [{ quantity: '1', note: '' }] }), rules)).toEqual([]);
  });

  it('reports a warning or information without making it an error', () => {
    const rules: FormRules<Model> = {
      title: [warn(maxLength(3, 'Long for a title.')), inform(minLength(10, 'Short.'))],
    };

    expect(validateModel(model(), rules).map((issue) => [issue.code, issue.severity])).toEqual([
      ['maxLength', 'warning'],
      ['minLength', 'info'],
    ]);
  });
});

describe('rule helpers', () => {
  it('treats unset, empty, whitespace and an empty list as blank for required', () => {
    const rule = required('x');

    for (const blank of [undefined, null, '', '  ', []]) {
      expect(rule(blank, { model: {}, path: 'p' })).not.toBeNull();
    }
    for (const present of ['a', 0, false, ['a']]) {
      expect(rule(present, { model: {}, path: 'p' })).toBeNull();
    }
  });

  it('leaves a blank value to required in every other rule', () => {
    const context = { model: {}, path: 'p' };

    expect(maxLength(1, 'x')('', context)).toBeNull();
    expect(minLength(5, 'x')('', context)).toBeNull();
    expect(range({ min: 1 }, 'x')('', context)).toBeNull();
    expect(pattern(/^a$/, 'x')('', context)).toBeNull();
  });

  it('checks a range on numbers and numeric text, and ignores text that is not a number', () => {
    const rule = range({ min: 1, max: 10 }, 'x');
    const context = { model: {}, path: 'p' };

    expect(rule(0, context)).not.toBeNull();
    expect(rule('11', context)).not.toBeNull();
    expect(rule(5, context)).toBeNull();
    expect(rule('abc', context)).toBeNull();
  });

  it('takes an http or https address and refuses any other scheme, a relative one, and one with credentials', () => {
    for (const address of ['https://example.com', 'http://example.com/a?b=c', '  https://example.com/about  ']) {
      expect(isWebAddress(address)).withContext(address).toBeTrue();
    }
    for (const address of ['example.com', '/about', 'ftp://example.com', 'mailto:sam@example.com', 'https://sam:secret@example.com', 'https://sam@example.com']) {
      expect(isWebAddress(address)).withContext(address).toBeFalse();
    }
  });

  it('leaves a blank address to required, like every other rule', () => {
    const rule = webAddress('x');
    const context = { model: {}, path: 'p' };

    expect(rule('', context)).toBeNull();
    expect(rule('example.com', context)?.code).toBe('webAddress');
    expect(rule('https://example.com', context)).toBeNull();
  });

  it('is not made stateful by a global regular expression', () => {
    const rule = pattern(/^a$/g, 'x');
    const context = { model: {}, path: 'p' };

    expect(rule('a', context)).toBeNull();
    expect(rule('a', context)).toBeNull();
    expect(rule('b', context)).not.toBeNull();
  });
});
