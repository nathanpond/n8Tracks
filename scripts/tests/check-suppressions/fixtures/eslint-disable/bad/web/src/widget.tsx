/* eslint-disable no-console */
export function Widget() {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const value: any = read();
  console.log(value); // eslint-disable-line no-console
  return (
    <div>
      {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
      <input autoFocus />
    </div>
  );
}
/* eslint-enable no-console */
