import { AppShell, Container, NavLink, Stack, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { useEffect } from 'react';
import { Link, Outlet, useLocation } from 'react-router';
import { AppHeader } from '../components/AppHeader';
import { UserMenu } from './UserMenu';

/** A sidebar entry: a link to one page, marked as the current page when it is. */
function SidebarLink({
  to,
  label,
  onNavigate,
}: {
  to: string;
  label: string;
  onNavigate: () => void;
}) {
  const { pathname } = useLocation();
  const active = pathname === to || pathname.startsWith(`${to}/`);

  return (
    <NavLink
      component={Link}
      to={to}
      label={label}
      active={active}
      aria-current={active ? 'page' : undefined}
      onClick={onNavigate}
    />
  );
}

/**
 * The signed-in shell: the header (sidebar toggle, product name, colour control, user menu), the
 * sidebar, and the current page. The sidebar lists Songs and Settings, and Settings has Account and
 * System; later stories add pages. On a narrow screen the sidebar is hidden until the toggle opens
 * it, and choosing a page closes it again.
 */
export function SignedInShell() {
  const [opened, { toggle, close }] = useDisclosure(false);
  const { pathname } = useLocation();

  // A page opened some other way (back, a link in a page) closes the sidebar on a narrow screen too.
  useEffect(() => {
    close();
  }, [pathname, close]);

  return (
    <AppShell
      header={{ height: 56 }}
      navbar={{ width: 220, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="md"
    >
      <AppHeader navigation={{ opened, toggle }}>
        <UserMenu />
      </AppHeader>
      <AppShell.Navbar p="xs" id="app-navigation" aria-label="Main">
        <Stack gap={4}>
          <SidebarLink to="/songs" label="Songs" onNavigate={close} />
          <Text
            id="navigation-settings"
            size="xs"
            fw={700}
            tt="uppercase"
            c="var(--n8-color-secondary-text)"
            px="sm"
            pt="sm"
          >
            Settings
          </Text>
          <div role="group" aria-labelledby="navigation-settings">
            <SidebarLink to="/settings/account" label="Account" onNavigate={close} />
            <SidebarLink to="/settings/system" label="System" onNavigate={close} />
          </div>
        </Stack>
      </AppShell.Navbar>
      <AppShell.Main>
        <Container size="md" px={0}>
          <Outlet />
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
