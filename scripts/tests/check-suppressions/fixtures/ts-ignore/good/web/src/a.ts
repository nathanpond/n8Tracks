// @ts-ignore: the package ships no types and none exist on DefinitelyTyped
import thing from 'untyped';

export const a = () => {
  // The property exists at run time; the vendor typings lag one release behind.
  /* @ts-ignore */
  return thing.missing;
};
