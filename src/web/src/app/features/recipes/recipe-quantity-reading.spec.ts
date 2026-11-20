import { splitReading } from './recipe-quantity-reading';

describe('splitReading', () => {
  it('cuts a reading into its figure and its unit', () => {
    expect(splitReading('1½ US cups')).toEqual({ amount: '1½', unit: 'US cups' });
    expect(splitReading('½ teaspoon')).toEqual({ amount: '½', unit: 'teaspoon' });
    expect(splitReading('0.02 g')).toEqual({ amount: '0.02', unit: 'g' });
    expect(splitReading('3/4 cup')).toEqual({ amount: '3/4', unit: 'cup' });
  });

  it('keeps a range together', () => {
    expect(splitReading('2–3 tbsp')).toEqual({ amount: '2–3', unit: 'tbsp' });
  });

  it('has no unit to say for a bare figure', () => {
    expect(splitReading('6')).toEqual({ amount: '6', unit: '' });
  });

  it('leaves text that does not start with a figure whole', () => {
    expect(splitReading('a pinch')).toEqual({ amount: 'a pinch', unit: '' });
  });
});
