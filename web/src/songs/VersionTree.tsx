import { ActionIcon, Badge, Group, Menu, Stack, Switch, Text, Title } from '@mantine/core';
import {
  useEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
  type ReactNode,
} from 'react';
import type { Version } from '../api/versions';
import { nestVersions, versionLabel, type VersionNode } from './versionNesting';

/** What can be done with a Version, from its actions menu in the tree or the selected Version's header. */
export interface VersionActions {
  onCreateFrom: (version: Version) => void;
  onMakeCurrent: (version: Version) => void;
  onSetArchived: (version: Version, archived: boolean) => void;
}

/** The tree's nodes in the order the arrow keys walk them: depth first, skipping collapsed branches. */
interface Visible {
  node: VersionNode;
  level: number;
  parentId: string | undefined;
}

function walk(
  nodes: VersionNode[],
  collapsed: ReadonlySet<string>,
  level = 1,
  parentId?: string,
): Visible[] {
  return nodes.flatMap((node) => [
    { node, level, parentId },
    ...(collapsed.has(node.version.id)
      ? []
      : walk(node.children, collapsed, level + 1, node.version.id)),
  ]);
}

/** The menu items for one Version; shared by the tree's per-node menu. */
function ActionItems({ version, actions }: { version: Version; actions: VersionActions }) {
  return (
    <>
      <Menu.Item
        onClick={() => {
          actions.onCreateFrom(version);
        }}
      >
        Create New Version From {version.number}
      </Menu.Item>
      {!version.current && (
        <Menu.Item
          onClick={() => {
            actions.onMakeCurrent(version);
          }}
        >
          Make current
        </Menu.Item>
      )}
      <Menu.Item
        onClick={() => {
          actions.onSetArchived(version, !version.archived);
        }}
      >
        {version.archived ? 'Unarchive' : 'Archive'}
      </Menu.Item>
    </>
  );
}

/**
 * A Song's Versions as an ARIA tree drawn from their numbers: each under its nearest drawn ancestor
 * (`1.1` under `1`; a Version whose parent is missing or hidden goes under the nearest one that is
 * drawn), siblings in numeric order, every branch expanded at first. The selected Version is
 * `aria-selected`; the current working Version is `aria-current` and marked. Archived Versions are
 * drawn, dimmed, only while "Show archived" is on; the current one is always drawn.
 *
 * One node is in the tab order at a time (roving tabindex). Up and Down move between drawn nodes,
 * Home and End go to the first and last, Right expands a branch or enters it, Left collapses it or
 * goes to the parent, and Enter or Space selects. Each node has an actions menu, opened from its
 * button (the next tab stop after the node) or with Shift+F10 or the context-menu key.
 */
export function VersionTree({
  versions,
  selectedId,
  showArchived,
  onShowArchived,
  onSelect,
  actions,
}: {
  versions: Version[];
  selectedId: string | undefined;
  showArchived: boolean;
  onShowArchived: (show: boolean) => void;
  onSelect: (version: Version) => void;
  actions: VersionActions;
}) {
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  const [focusedId, setFocusedId] = useState<string | undefined>();
  const [menuFor, setMenuFor] = useState<string | undefined>();
  const items = useRef(new Map<string, HTMLElement>());
  const hadFocus = useRef(false);

  const roots = nestVersions(
    versions,
    (version) => showArchived || !version.archived || version.current,
  );
  const visible = walk(roots, collapsed);
  const tabbableId =
    [focusedId, selectedId]
      .map((id) => visible.find((entry) => entry.node.version.id === id))
      .find((entry) => entry !== undefined)?.node.version.id ?? visible[0]?.node.version.id;

  // A node that held focus and is no longer drawn (archived while "Show archived" is off) hands
  // focus to the node now in the tab order, so a keyboard user is not dropped on the page body.
  const visibleKey = visible.map((entry) => entry.node.version.id).join(' ');
  useEffect(() => {
    const active = document.activeElement;
    if (hadFocus.current && (active === null || active === document.body) && tabbableId) {
      items.current.get(tabbableId)?.focus();
    }
  }, [visibleKey, tabbableId]);

  const focusNode = (id: string | undefined) => {
    if (id === undefined) {
      return;
    }
    setFocusedId(id);
    items.current.get(id)?.focus();
  };

  const setExpanded = (id: string, expanded: boolean) => {
    setCollapsed((previous) => {
      const next = new Set(previous);
      if (expanded) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const keys = (event: KeyboardEvent<HTMLElement>, index: number) => {
    // Keys pressed in the node's actions button or its menu (a portal, still a React child) are theirs.
    if (event.target !== event.currentTarget) {
      return;
    }
    const entry = visible[index];
    if (entry === undefined) {
      return;
    }
    const { version, children } = entry.node;
    const expanded = children.length > 0 && !collapsed.has(version.id);
    let handled = true;
    switch (event.key) {
      case 'ArrowDown':
        focusNode(visible[index + 1]?.node.version.id);
        break;
      case 'ArrowUp':
        focusNode(visible[index - 1]?.node.version.id);
        break;
      case 'Home':
        focusNode(visible[0]?.node.version.id);
        break;
      case 'End':
        focusNode(visible[visible.length - 1]?.node.version.id);
        break;
      case 'ArrowRight':
        if (children.length > 0) {
          if (expanded) {
            focusNode(children[0]?.version.id);
          } else {
            setExpanded(version.id, true);
          }
        }
        break;
      case 'ArrowLeft':
        if (expanded) {
          setExpanded(version.id, false);
        } else {
          focusNode(entry.parentId);
        }
        break;
      case 'Enter':
      case ' ':
        onSelect(version);
        break;
      case 'ContextMenu':
        setMenuFor(version.id);
        break;
      case 'F10':
        if (event.shiftKey) {
          setMenuFor(version.id);
        } else {
          handled = false;
        }
        break;
      default:
        handled = false;
    }
    if (handled) {
      event.preventDefault();
    }
  };

  const click = (event: MouseEvent<HTMLElement>, node: VersionNode) => {
    // A click in the actions button or its menu (a portal) is not a selection.
    const target = event.target;
    if (
      !(target instanceof Element) ||
      !event.currentTarget.contains(target) ||
      target.closest('button') !== null
    ) {
      return;
    }
    const { version, children } = node;
    setFocusedId(version.id);
    if (children.length > 0 && target.closest('[data-disclosure]') !== null) {
      setExpanded(version.id, collapsed.has(version.id));
      return;
    }
    onSelect(version);
  };

  const groupId = (version: Version) => `version-group-${version.id}`;

  const draw = (nodes: VersionNode[], level: number): ReactNode =>
    nodes.map((node, position) => {
      const { version, children } = node;
      const index = visible.findIndex((entry) => entry.node.version.id === version.id);
      const branch = children.length > 0;
      const expanded = branch && !collapsed.has(version.id);
      const selected = version.id === selectedId;
      const tabbable = version.id === tabbableId;
      return (
        <div key={version.id} role="none">
          <div
            role="treeitem"
            ref={(element) => {
              if (element) {
                items.current.set(version.id, element);
              } else {
                items.current.delete(version.id);
              }
            }}
            className="mantine-focus-auto"
            tabIndex={tabbable ? 0 : -1}
            aria-label={versionLabel(version)}
            aria-level={level}
            aria-setsize={nodes.length}
            aria-posinset={position + 1}
            aria-selected={selected}
            aria-current={version.current ? 'true' : undefined}
            aria-expanded={branch ? expanded : undefined}
            aria-owns={expanded ? groupId(version) : undefined}
            data-version-number={version.number}
            data-archived={version.archived || undefined}
            onKeyDown={(event) => {
              keys(event, index);
            }}
            onClick={(event) => {
              click(event, node);
            }}
            onFocus={(event) => {
              if (event.target === event.currentTarget) {
                setFocusedId(version.id);
              }
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 4,
              cursor: 'pointer',
              paddingBlock: 2,
              paddingInline: 'var(--mantine-spacing-xs)',
              borderRadius: 'var(--mantine-radius-sm)',
              background: selected ? 'var(--mantine-color-default-hover)' : undefined,
              borderInlineStart: selected
                ? '3px solid var(--mantine-primary-color-filled)'
                : '3px solid transparent',
              color: version.archived ? 'var(--n8-color-secondary-text)' : undefined,
              fontStyle: version.archived ? 'italic' : undefined,
            }}
          >
            {/* The disclosure arrow is for the mouse (a click on it expands or collapses); keyboard
                users have Left and Right. */}
            <Text span aria-hidden="true" w="1em" ta="center" data-disclosure>
              {branch ? (expanded ? '▾' : '▸') : ''}
            </Text>
            <Group gap={6} wrap="nowrap" style={{ flex: 1, minWidth: 0 }}>
              <Text span fw={700} ff="monospace">
                {version.number}
              </Text>
              {version.name !== null && (
                <Text span truncate>
                  {version.name}
                </Text>
              )}
              {version.current && (
                <Badge size="sm" variant="filled" radius="sm" tt="none">
                  Current
                </Badge>
              )}
              {version.archived && (
                <Text span size="xs">
                  (archived)
                </Text>
              )}
            </Group>
            {/* No focus placeholder: Mantine's is a focusable element with role="presentation"
                inside the menu, which axe reports as a child a menu may not have. */}
            <Menu
              position="bottom-end"
              withinPortal
              withInitialFocusPlaceholder={false}
              hideDetached={false}
              opened={menuFor === version.id}
              onChange={(opened) => {
                setMenuFor(opened ? version.id : undefined);
              }}
              onClose={() => {
                items.current.get(version.id)?.focus();
              }}
            >
              <Menu.Target>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="sm"
                  tabIndex={tabbable ? 0 : -1}
                  aria-label={`Actions for Version ${version.number}`}
                >
                  <span aria-hidden="true">⋯</span>
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <ActionItems version={version} actions={actions} />
              </Menu.Dropdown>
            </Menu>
          </div>
          {expanded && (
            <div
              role="group"
              id={groupId(version)}
              style={{ paddingInlineStart: 'var(--mantine-spacing-md)' }}
            >
              {draw(children, level + 1)}
            </div>
          )}
        </div>
      );
    });

  return (
    <Stack gap="xs" component="section" aria-labelledby="versions-heading">
      <Title order={3} size="h5" id="versions-heading">
        Versions
      </Title>
      <Switch
        size="sm"
        label="Show archived"
        checked={showArchived}
        onChange={(event) => {
          onShowArchived(event.currentTarget.checked);
        }}
      />
      <Text id="versions-keys" size="xs" c="var(--n8-color-secondary-text)">
        Arrow keys move through the tree, Enter selects, and Shift+F10 opens a Version’s actions.
      </Text>
      <div
        role="tree"
        aria-labelledby="versions-heading"
        aria-describedby="versions-keys"
        onFocus={() => {
          hadFocus.current = true;
        }}
        onBlur={(event) => {
          if (event.relatedTarget !== null && !event.currentTarget.contains(event.relatedTarget)) {
            hadFocus.current = false;
          }
        }}
      >
        {draw(roots, 1)}
      </div>
    </Stack>
  );
}
