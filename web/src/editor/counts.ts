/** A count as the page writes it: `5,000`. */
export function formatCount(count: number): string {
  return count.toLocaleString('en-US');
}
