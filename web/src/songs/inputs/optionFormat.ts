// How option values are written on the page and announced to a screen reader.

/** A choice's value as the page writes it: `male` is "Male". */
export function choiceLabel(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

/** Seconds as a length: `2:05`. */
export function formatDuration(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${String(minutes)}:${String(seconds % 60).padStart(2, '0')}`;
}

/** Seconds as a screen reader says them: "2 minutes 5 seconds". */
export function spokenDuration(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  const parts = [
    minutes > 0 && `${String(minutes)} ${minutes === 1 ? 'minute' : 'minutes'}`,
    (rest > 0 || minutes === 0) && `${String(rest)} ${rest === 1 ? 'second' : 'seconds'}`,
  ];
  return parts.filter((part): part is string => typeof part === 'string').join(' ');
}
