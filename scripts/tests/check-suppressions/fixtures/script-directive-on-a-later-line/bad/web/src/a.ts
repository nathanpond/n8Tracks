/*
  eslint-disable no-console
*/
console.log('a');
/*
 * @ts-ignore */
export const a: number = 'a';
/* eslint
   no-alert: 0 */
alert('a');
/*
   @ts-expect-error */
export const b: number = 'b';
