// A canary for scripts/check-canaries.sh; never part of the product. Each marked line breaks
// one lint rule; the check requires `npm run lint` to fail and to report every one of them.
export function canaryLint(work: () => Promise<void>): unknown {
  debugger; // canary: error no-debugger
  work(); // canary: error @typescript-eslint/no-floating-promises
  const loose: any = JSON.parse('{}'); // canary: error @typescript-eslint/no-explicit-any
  const list: Array<string> = []; // canary: error @typescript-eslint/array-type
  return [loose, list];
}
