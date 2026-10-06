import { ActionIcon, Group } from '@mantine/core';

/** A list row's move and remove controls, named by the row's noun and position. */
export function RowControls({
  noun,
  index,
  count,
  onMove,
  onRemove,
}: {
  noun: string;
  index: number;
  count: number;
  onMove?: (from: number, to: number) => void;
  onRemove: (index: number) => void;
}) {
  const position = String(index + 1);
  return (
    <Group gap={4} wrap="nowrap" mt={4}>
      {onMove !== undefined && (
        <>
          <ActionIcon
            variant="default"
            aria-label={`Move ${noun} ${position} up`}
            disabled={index === 0}
            onClick={() => {
              onMove(index, index - 1);
            }}
          >
            <span aria-hidden="true">↑</span>
          </ActionIcon>
          <ActionIcon
            variant="default"
            aria-label={`Move ${noun} ${position} down`}
            disabled={index === count - 1}
            onClick={() => {
              onMove(index, index + 1);
            }}
          >
            <span aria-hidden="true">↓</span>
          </ActionIcon>
        </>
      )}
      <ActionIcon
        variant="default"
        aria-label={`Remove ${noun} ${position}`}
        onClick={() => {
          onRemove(index);
        }}
      >
        <span aria-hidden="true">×</span>
      </ActionIcon>
    </Group>
  );
}
