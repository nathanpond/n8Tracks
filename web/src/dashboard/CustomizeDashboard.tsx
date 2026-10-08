import {
  closestCenter,
  DndContext,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type Announcements,
  type DragEndEvent,
  type UniqueIdentifier,
} from '@dnd-kit/core';
import {
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import {
  ActionIcon,
  Button,
  Checkbox,
  Group,
  Modal,
  Paper,
  Stack,
  Text,
  VisuallyHidden,
} from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import type { DashboardSectionKey } from '../api/dashboard';
import {
  defaultSections,
  resetDashboardLayout,
  saveDashboardLayout,
  type DashboardLayout,
  type SectionPlacement,
} from '../api/dashboardSettings';
import { movedText, moveSection } from './customizeRules';

function SectionRow({
  section,
  index,
  total,
  title,
  onToggle,
  onMove,
  buttons,
}: {
  section: SectionPlacement;
  index: number;
  total: number;
  title: string;
  onToggle: (shown: boolean) => void;
  onMove: (to: number) => void;
  buttons: (key: string, button: HTMLButtonElement | null) => void;
}) {
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition } =
    useSortable({ id: section.key });
  return (
    <Paper
      component="li"
      ref={setNodeRef}
      withBorder
      p="xs"
      style={{ transform: CSS.Transform.toString(transform), transition }}
      data-testid="customize-section"
      data-section={section.key}
    >
      <Group gap="xs" wrap="nowrap" justify="space-between">
        <Group gap="xs" wrap="nowrap">
          <ActionIcon
            variant="subtle"
            ref={setActivatorNodeRef}
            {...attributes}
            {...listeners}
            aria-label={`Drag ${title}`}
            style={{ cursor: 'grab', touchAction: 'none' }}
          >
            <span aria-hidden="true">⠿</span>
          </ActionIcon>
          <Checkbox
            label={title}
            checked={!section.hidden}
            onChange={(event) => {
              onToggle(event.currentTarget.checked);
            }}
          />
        </Group>
        <Group gap={4} wrap="nowrap">
          <Button
            variant="default"
            size="compact-xs"
            ref={(button) => {
              buttons(`${section.key}:up`, button);
            }}
            disabled={index === 0}
            aria-label={`Move ${title} up`}
            onClick={() => {
              onMove(index - 1);
            }}
          >
            Move up
          </Button>
          <Button
            variant="default"
            size="compact-xs"
            ref={(button) => {
              buttons(`${section.key}:down`, button);
            }}
            disabled={index === total - 1}
            aria-label={`Move ${title} down`}
            onClick={() => {
              onMove(index + 1);
            }}
          >
            Move down
          </Button>
        </Group>
      </Group>
    </Paper>
  );
}

/** How saving went, when it did not. */
type SaveProblem = 'conflict' | 'failed' | null;

/**
 * Customize (#230): a draft of the dashboard's arrangement in a dialog. Each section can be shown or
 * hidden, and moved up or down with its buttons or by dragging its handle (with the pointer, or with
 * the keyboard: Space, the arrow keys, Space); each move is announced. Reset puts the draft back to
 * the default order with every section shown; Save applies the draft (after Reset, by clearing the
 * saved arrangement, so a later version's default order applies), and Cancel discards it. A save
 * from a stale revision is refused and the user is asked to reload.
 */
export function CustomizeDashboard({
  opened,
  layout,
  titles,
  onClose,
  onSaved,
  onReload,
}: {
  opened: boolean;
  layout: DashboardLayout;
  titles: Record<DashboardSectionKey, string>;
  onClose: () => void;
  onSaved: (layout: DashboardLayout) => void;
  onReload: () => void;
}) {
  // The dialog's content is mounted only while it is open, so every opening, and the arrangement a
  // reload reads, starts a new draft from the arrangement as it is now.
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title="Customize the dashboard"
      size="lg"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <CustomizeBody
        key={`${String(layout.revision)}:${layout.sections.map((section) => `${section.key}${section.hidden ? '-' : '+'}`).join(',')}`}
        layout={layout}
        titles={titles}
        onClose={onClose}
        onSaved={onSaved}
        onReload={onReload}
      />
    </Modal>
  );
}

function CustomizeBody({
  layout,
  titles,
  onClose,
  onSaved,
  onReload,
}: {
  layout: DashboardLayout;
  titles: Record<DashboardSectionKey, string>;
  onClose: () => void;
  onSaved: (layout: DashboardLayout) => void;
  onReload: () => void;
}) {
  const [draft, setDraft] = useState<SectionPlacement[]>(layout.sections);
  const [cleared, setCleared] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState<SaveProblem>(null);
  const buttons = useRef(new Map<string, HTMLButtonElement>());
  const focusAfterMove = useRef<string | null>(null);

  // A move by button keeps focus on that section's button; at an end, on its other button.
  useEffect(() => {
    const key = focusAfterMove.current;
    focusAfterMove.current = null;
    if (key !== null) {
      buttons.current.get(key)?.focus();
    }
  }, [draft]);

  const sensors = useSensors(
    useSensor(PointerSensor),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const titleOf = (id: UniqueIdentifier) => titles[id as DashboardSectionKey];
  const positionOf = (id: UniqueIdentifier | undefined) =>
    id === undefined ? 0 : draft.findIndex((section) => section.key === id) + 1;
  const announcements: Announcements = {
    onDragStart: ({ active }) =>
      `Picked up ${titleOf(active.id)}, at position ${String(positionOf(active.id))} of ${String(draft.length)}.`,
    onDragOver: ({ active, over }) =>
      over === null
        ? `${titleOf(active.id)} is no longer over a position.`
        : `${titleOf(active.id)} is over position ${String(positionOf(over.id))} of ${String(draft.length)}.`,
    onDragEnd: ({ active, over }) =>
      over === null
        ? `${titleOf(active.id)} was dropped where it was.`
        : movedText(titleOf(active.id), positionOf(over.id), draft.length),
    onDragCancel: ({ active }) => `Moving ${titleOf(active.id)} was cancelled.`,
  };

  const move = (from: number, to: number, focusKey?: string) => {
    const next = moveSection(draft, from, to);
    if (next === draft) {
      return;
    }
    const moved = draft[from];
    if (moved === undefined) {
      return;
    }
    setDraft(next);
    setCleared(false);
    setAnnouncement(movedText(titles[moved.key], to + 1, next.length));
    if (focusKey !== undefined) {
      const atEnd = to === 0 ? 'down' : to === next.length - 1 ? 'up' : null;
      focusAfterMove.current = atEnd === null ? focusKey : `${moved.key}:${atEnd}`;
    }
  };

  const onDragEnd = ({ active, over }: DragEndEvent) => {
    if (over === null || active.id === over.id) {
      return;
    }
    const from = draft.findIndex((section) => section.key === active.id);
    const to = draft.findIndex((section) => section.key === over.id);
    const next = moveSection(draft, from, to);
    if (next !== draft) {
      setDraft(next);
      setCleared(false);
    }
  };

  const save = async () => {
    setSaving(true);
    setProblem(null);
    const result = cleared
      ? await resetDashboardLayout(layout.revision)
      : await saveDashboardLayout(layout.revision, draft);
    setSaving(false);
    switch (result.kind) {
      case 'saved':
        onSaved(result.record);
        onClose();
        return;
      case 'conflict':
        setProblem('conflict');
        return;
      default:
        setProblem('failed');
    }
  };

  const everyHidden = draft.every((section) => section.hidden);

  return (
    <Stack gap="sm">
      <Text size="sm" id="customize-help">
        Show or hide each section, and move it up or down, with its buttons or by dragging its
        handle. With the keyboard, press Space on a handle, move with the arrow keys, and press
        Space again to drop. Quick actions are always shown.
      </Text>
      <DndContext
        sensors={sensors}
        collisionDetection={closestCenter}
        onDragEnd={onDragEnd}
        accessibility={{
          announcements,
          screenReaderInstructions: {
            draggable:
              'To pick up a section, press Space or Enter. Move it with the arrow keys, press Space or Enter again to drop it, or Escape to cancel.',
          },
        }}
      >
        <SortableContext
          items={draft.map((section) => section.key)}
          strategy={verticalListSortingStrategy}
        >
          <Stack
            component="ol"
            gap="xs"
            m={0}
            p={0}
            style={{ listStyle: 'none' }}
            aria-label="Dashboard sections, in order"
          >
            {draft.map((section, index) => (
              <SectionRow
                key={section.key}
                section={section}
                index={index}
                total={draft.length}
                title={titles[section.key]}
                buttons={(key, button) => {
                  if (button === null) {
                    buttons.current.delete(key);
                  } else {
                    buttons.current.set(key, button);
                  }
                }}
                onToggle={(shown) => {
                  setDraft(
                    draft.map((item) =>
                      item.key === section.key ? { ...item, hidden: !shown } : item,
                    ),
                  );
                  setCleared(false);
                }}
                onMove={(to) => {
                  move(index, to, `${section.key}:${to < index ? 'up' : 'down'}`);
                }}
              />
            ))}
          </Stack>
        </SortableContext>
      </DndContext>
      <VisuallyHidden role="status" aria-live="polite" data-testid="customize-announcement">
        {announcement}
      </VisuallyHidden>
      {everyHidden && (
        <Text size="sm" data-testid="customize-all-hidden">
          Every section is hidden: the dashboard will show only the quick actions.
        </Text>
      )}
      {problem === 'conflict' && (
        <Stack gap="xs" align="flex-start" role="alert" data-testid="customize-conflict">
          <Text size="sm">
            The arrangement was changed in another tab or browser since you opened this. Reload it
            to see that one; what you changed here is not saved.
          </Text>
          <Button variant="default" size="xs" onClick={onReload}>
            Reload the arrangement
          </Button>
        </Stack>
      )}
      {problem === 'failed' && (
        <Text size="sm" role="alert" data-testid="customize-failed">
          The arrangement could not be saved. Try again.
        </Text>
      )}
      <Group justify="space-between">
        <Button
          variant="subtle"
          onClick={() => {
            setDraft(defaultSections(layout));
            setCleared(true);
            setAnnouncement('Reset to the default order, with every section shown.');
          }}
        >
          Reset
        </Button>
        <Group gap="xs">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button
            loading={saving}
            disabled={problem === 'conflict'}
            onClick={() => {
              void save();
            }}
          >
            Save
          </Button>
        </Group>
      </Group>
    </Stack>
  );
}
