// eslint-disable-next-line no-console -- TODO: explain later
console.log(1);
// eslint-disable-next-line no-console --
console.log(2);
// eslint-disable-next-line no-console -- fixme
console.log(3);
// eslint-disable-next-line no-console -- short
console.log(4);
// eslint-disable-next-line no-console -- N/A
console.log(5);
// @ts-expect-error -- pending a fix upstream in the library
const a: number = 'x';
// @ts-ignore --
const b: number = 'y';
// eslint-disable-next-line no-console -- the line above the directive is itself a directive
// @ts-expect-error
console.log(a, b, undefinedName);
