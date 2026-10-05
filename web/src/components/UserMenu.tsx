import { Button, Menu } from '@mantine/core';
import { useSignedInSession } from '../auth/sessionContext';

/** The signed-in user's menu: their username, and the two ways to sign out. Nothing when signed out. */
export function UserMenu() {
  const session = useSignedInSession();
  if (!session) {
    return null;
  }

  return (
    // No focus placeholder: Mantine's is a focusable element with role="presentation" inside the
    // menu, which axe reports as a child a menu may not have (aria-required-children).
    <Menu position="bottom-end" withinPortal withInitialFocusPlaceholder={false}>
      <Menu.Target>
        <Button variant="default" size="xs" data-testid="user-menu">
          {session.username}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item
          onClick={() => {
            void session.signOut();
          }}
        >
          Sign out
        </Menu.Item>
        <Menu.Item
          onClick={() => {
            void session.signOutEverywhere();
          }}
        >
          Sign out everywhere
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
