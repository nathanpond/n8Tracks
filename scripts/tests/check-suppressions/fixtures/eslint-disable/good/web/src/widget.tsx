/* eslint-disable no-console -- this module is the development console bridge */
export function Widget() {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- the vendor type is wrong here
  const value: any = read();
  console.log(value); // eslint-disable-line no-console -- surfaced to the developer on purpose
  return (
    <div>
      {/* eslint-disable-next-line jsx-a11y/no-autofocus -- a dialog must take focus when it opens */}
      <input autoFocus />
    </div>
  );
}
/* eslint-enable no-console */
