// A canary for scripts/check-canaries.sh; never part of the product. Each marked line is a type
// error only while one strictness setting is on; the check requires `npm run typecheck` to fail
// and to report every one of them, on that line, with that code.
export function implicitAny(value) { // canary: TS7006
  return String(value);
}

export function possiblyUndefined(text: string | undefined): number {
  return text.length; // canary: TS18048
}

export class Uninitialized {
  name: string; // canary: TS2564
}

export function unknownInCatch(): string {
  try {
    return 'canary';
  } catch (error) {
    return error.message; // canary: TS18046
  }
}

export function implicitThis(): unknown {
  return function () {
    return this; // canary: TS2683
  };
}

export function bindCallApply(): number {
  const double = (value: number): number => value * 2;
  return double.call(undefined, 'canary'); // canary: TS2345
}

export function functionTypes(narrow: (value: string) => void): (value: string | number) => void {
  const wide: (value: string | number) => void = narrow; // canary: TS2322
  return wide;
}

export function builtinIteratorReturn(): string {
  const result = ['canary'].values().next();
  if (result.done) {
    return result.value; // canary: TS2322
  }
  return result.value;
}

export function uncheckedIndexedAccess(list: number[]): number {
  return list[0]; // canary: TS2322
}

export function unusedLocal(): void {
  const unused = 1; // canary: TS6133
}

export function unusedParameter(unused: number): void {} // canary: TS6133

export function fallthrough(value: number): number {
  let total = 0;
  switch (value) {
    case 1: // canary: TS7029
      total += 1;
    case 2:
      total += 2;
  }
  return total;
}

export class Base {
  run(): void {}
}

export class Derived extends Base {
  run(): void {} // canary: TS4114
}
