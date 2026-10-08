import { arrayMove } from '@dnd-kit/sortable';
import type { SectionPlacement } from '../api/dashboardSettings';

/** What a move says to a screen reader: "Unmatched Files moved to position 1 of 6." */
export function movedText(title: string, position: number, total: number): string {
  return `${title} moved to position ${String(position)} of ${String(total)}.`;
}

/** The arrangement with the section at `from` moved to `to`; the same array when nothing moves. */
export function moveSection(
  sections: SectionPlacement[],
  from: number,
  to: number,
): SectionPlacement[] {
  return to < 0 || to >= sections.length || from < 0 || from >= sections.length || from === to
    ? sections
    : arrayMove(sections, from, to);
}
