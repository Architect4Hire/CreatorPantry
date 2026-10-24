/**
 * Money as the reader's own locale writes it, in the catalogue's currency.
 *
 * One formatter for the whole public page: a price, a saving and a pack's cost are the same kind of figure and
 * have to be written the same way. A whole number is written without the pence a price like $19.00 would
 * otherwise carry.
 *
 * The currency is the catalogue's, never a default of the page's own — a figure in the wrong currency is worse
 * than no figure. An empty currency means an unpublished catalogue, where nothing is shown anyway.
 */
export function formatMoney(currency: string, amount: number): string {
  if (!currency) return '';

  return new Intl.NumberFormat(undefined, {
    style: 'currency',
    currency,
    maximumFractionDigits: Number.isInteger(amount) ? 0 : 2,
  }).format(amount);
}
