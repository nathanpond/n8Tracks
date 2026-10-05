import { Button, Text } from '@mantine/core';
import { Notice } from '../components/Notice';

/** What the notice says about a frozen Version, as the story words it. */
export const FROZEN_NOTICE_TEXT =
  'This Version has a Generation, so its lyrics and settings can no longer be changed. Create a new Version to keep working.';

/**
 * The notice above a frozen Version's lyrics and styles: why they are read only, and the way on,
 * "Create New Version From" this one. When text was typed that the Version could not take (it froze
 * while the editor was open), `carried` says so: that text starts the new Version instead of the
 * stored one. The name and notes stay editable, so the notice says nothing about them.
 */
export function FrozenNotice({
  number,
  carried,
  onCreate,
}: {
  number: string;
  /** Unsaved lyrics or styles the Version could not take, waiting to start a new Version. */
  carried: boolean;
  onCreate: () => void;
}) {
  return (
    <div data-testid="frozen-notice">
      <Notice title="Lyrics and styles are locked">
        <Text size="sm">{FROZEN_NOTICE_TEXT}</Text>
        {carried && (
          <Text size="sm" fw={700}>
            Your unsaved changes to the lyrics and styles could not be saved to this Version. They
            are kept for the new Version, which starts with them; a copy is also kept in History.
          </Text>
        )}
        <div>
          <Button size="compact-sm" mt={4} onClick={onCreate}>
            Create New Version From {number}
          </Button>
        </div>
      </Notice>
    </div>
  );
}
