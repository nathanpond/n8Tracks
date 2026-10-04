// A canary for scripts/check-canaries.sh; never part of the product. It breaks only a rule set
// to "warn", so linting fails on it only while warnings fail the lint (--max-warnings 0).
import { useEffect } from 'react';

export function CanaryWarning({ value }: { value: number }) {
  useEffect(() => {
    document.title = String(value);
  }, []); // canary: warning react-hooks/exhaustive-deps
  return <p>{value}</p>;
}
