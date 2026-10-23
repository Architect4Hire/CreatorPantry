import { HttpErrorResponse } from '@angular/common/http';

import { decodeFieldErrors, problemCodeOf, problemMessageOf, statusCodeOf } from './problem-details';

function refusal(body: unknown, status = 400): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('problem-details', () => {
  it('reads the status, code and title of a refusal', () => {
    const error = refusal({ title: 'Check the recipe.', code: 'recipes.recipe.invalid_request' });

    expect(statusCodeOf(error)).toBe(400);
    expect(problemCodeOf(error)).toBe('recipes.recipe.invalid_request');
    expect(problemMessageOf(error)).toBe('Check the recipe.');
  });

  it('answers null for anything that is not a refusal with a body', () => {
    for (const error of [new Error('x'), 'oops', null, refusal(null), refusal('<html>'), refusal({ code: 3, title: ' ' })]) {
      expect(problemCodeOf(error)).toBeNull();
      expect(problemMessageOf(error)).toBeNull();
    }
    expect(statusCodeOf(new Error('x'))).toBeNull();
  });

  it('decodes field errors and drops any entry that is not a list of sentences', () => {
    const error = refusal({ errors: { title: ['Name it.'], servings: 'bad', 'lines[0].quantity': ['a', 1] } });

    expect(decodeFieldErrors(error)).toEqual({ title: ['Name it.'] });
  });

  it('decodes no field errors from a body without them', () => {
    expect(decodeFieldErrors(refusal({ title: 'x' }))).toEqual({});
    expect(decodeFieldErrors(refusal({ errors: null }))).toEqual({});
    expect(decodeFieldErrors(new Error('x'))).toEqual({});
  });
});
