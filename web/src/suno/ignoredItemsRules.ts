import type { IgnoredItem, IgnoredStatus } from '../api/sunoIgnored';
import { formatDate } from '../api/timeZone';

/** Each status as the filter names it. */
export const STATUS_LABELS: Record<IgnoredStatus, string> = {
  present: 'Present',
  trashed: 'In Trash',
  missing: 'Missing',
  not_seen: 'Not seen',
};

/** "1 item", "3 items". */
export function itemCountText(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'item' : 'items'}`;
}

/** An item's Suno status as the list shows it; "Not seen since" with the date a sync last included it. */
export function statusText(item: IgnoredItem, timeZone: string): string {
  if (item.status === null) {
    return 'Not seen by a sync yet';
  }
  if (item.status === 'not_seen') {
    return item.lastSeenAt === null
      ? 'Not seen since it was ignored'
      : `Not seen since ${formatDate(item.lastSeenAt, timeZone)}`;
  }
  return STATUS_LABELS[item.status];
}
