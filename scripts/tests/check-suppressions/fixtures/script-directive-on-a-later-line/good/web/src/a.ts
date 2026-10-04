/*
  eslint-disable no-console -- the command-line entry point prints its results
*/
console.log('a');
/* The fixture assigns the wrong type on purpose.
 * @ts-ignore */
export const a: number = 'a';
/* eslint
   no-alert: 0 -- the kiosk build has no dialog component */
alert('a');
/*
   @ts-expect-error the fixture assigns the wrong type on purpose */
export const b: number = 'b';
/* A block comment may say that eslint-disable and @ts-ignore are not used here. */
