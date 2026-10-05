import { useEffect, useState } from 'react';
import { resolveAppUrl } from './baseUrl';

/** The browser's own time zone, used until the configured one is known or when it cannot be read. */
function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/** Whether the runtime knows a time zone by this ID. */
function isKnownTimeZone(timeZone: string): boolean {
  try {
    new Intl.DateTimeFormat(undefined, { timeZone });
    return true;
  } catch {
    return false;
  }
}

/**
 * The configured time zone (the `TZ` setting, which the health report names), read once. Until it
 * is known, or if it cannot be read or is unknown to the browser, the browser's own zone.
 */
export function useConfiguredTimeZone(): string {
  const [timeZone, setTimeZone] = useState(browserTimeZone);

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      try {
        const response = await fetch(resolveAppUrl('health'), {
          headers: { Accept: 'application/json' },
          signal: controller.signal,
        });
        const body: unknown = await response.json();
        if (
          typeof body === 'object' &&
          body !== null &&
          'timeZone' in body &&
          typeof body.timeZone === 'string' &&
          isKnownTimeZone(body.timeZone) &&
          !controller.signal.aborted
        ) {
          setTimeZone(body.timeZone);
        }
      } catch {
        // The browser's zone stays.
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, []);

  return timeZone;
}

/** A UTC ISO 8601 time as a date and time in `timeZone`, in the browser's locale. */
export function formatDateTime(utc: string, timeZone: string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone,
  }).format(new Date(utc));
}

const RELATIVE_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 60 * 60],
  ['month', 30 * 24 * 60 * 60],
  ['week', 7 * 24 * 60 * 60],
  ['day', 24 * 60 * 60],
  ['hour', 60 * 60],
  ['minute', 60],
];

/**
 * A UTC ISO 8601 time relative to `now`, in the browser's locale: "5 minutes ago", "yesterday".
 * Under a minute is "now". The largest whole unit is used, so 90 minutes is "1 hour ago".
 */
export function formatRelativeTime(utc: string, now: Date = new Date()): string {
  const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  const seconds = (new Date(utc).getTime() - now.getTime()) / 1000;
  for (const [unit, length] of RELATIVE_UNITS) {
    if (Math.abs(seconds) >= length) {
      return format.format(Math.trunc(seconds / length), unit);
    }
  }
  return format.format(0, 'second');
}
