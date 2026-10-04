// @ts-expect-error -- the test passes a wrong type on purpose
const a: number = 'x';

/*
 * The runtime check below is what this test exercises,
 * so the compiler must be told to let the bad value through.
 */
// @ts-expect-error
const b: number = 'y';
export { a, b };
