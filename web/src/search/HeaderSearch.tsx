import { ActionIcon, Box, Popover, TextInput } from '@mantine/core';
import { useEffect, useRef, useState, type SyntheticEvent } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { SEARCH_MAXIMUM_LENGTH } from '../api/songs';
import { isTypingTarget } from '../player/compare';
import {
  HEADER_SEARCH_ID,
  headerSearchAddress,
  isSearchShortcut,
  SONGS_SEARCH_ID,
} from './searchRules';

/** A magnifying glass, drawn in the text colour; decorative, the control is named in words. */
function SearchIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <circle cx="10.5" cy="10.5" r="6.5" fill="none" stroke="currentColor" strokeWidth="2.2" />
      <line
        x1="15.5"
        y1="15.5"
        x2="21"
        y2="21"
        stroke="currentColor"
        strokeWidth="2.2"
        strokeLinecap="round"
      />
    </svg>
  );
}

/** The search form: its box searches on Enter only. */
function SearchForm({
  text,
  onText,
  onSearch,
  inputRef,
  inputId,
}: {
  text: string;
  onText: (text: string) => void;
  onSearch: () => void;
  inputRef?: React.Ref<HTMLInputElement>;
  inputId?: string;
}) {
  const submit = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    onSearch();
  };
  return (
    <form role="search" aria-label="Search" onSubmit={submit} style={{ display: 'flex' }}>
      <TextInput
        id={inputId}
        ref={inputRef}
        size="xs"
        aria-label="Search Songs"
        aria-keyshortcuts="/ Control+K Meta+K"
        placeholder="Search Songs…"
        value={text}
        maxLength={SEARCH_MAXIMUM_LENGTH}
        onChange={(event) => {
          onText(event.currentTarget.value);
        }}
        spellCheck={false}
        autoComplete="off"
        data-autofocus
        style={{ flex: '0 1 16rem', minWidth: '8rem' }}
      />
    </form>
  );
}

/**
 * The header's search box (#224), on every signed-in page: Enter opens the Songs table with the
 * results for the text, starting afresh (none of the table's filters, sort, or page), or the
 * unsearched table for no text; text beyond 200 characters is cut. On the Songs page it shows the
 * active search. `/` and Ctrl+K (⌘K) focus it, or on the Songs page the table's own box, except
 * while typing in a field or the editor. On a narrow screen it is an icon that opens the box.
 */
export function HeaderSearch() {
  const navigate = useNavigate();
  const { pathname, search } = useLocation();
  const active = pathname === '/songs' ? (new URLSearchParams(search).get('search') ?? '') : null;
  const [text, setText] = useState(active ?? '');
  const [mirrored, setMirrored] = useState(active);
  const [opened, setOpened] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  // On the Songs page the box follows the address: a search made there or a step back shows here.
  if (active !== mirrored) {
    setMirrored(active);
    if (active !== null) {
      setText(active);
    }
  }

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (
        event.defaultPrevented ||
        event.repeat ||
        !isSearchShortcut(event) ||
        isTypingTarget(event.target)
      ) {
        return;
      }
      event.preventDefault();
      const table = document.getElementById(SONGS_SEARCH_ID);
      const header = inputRef.current;
      const target =
        table instanceof HTMLInputElement
          ? table
          : header !== null && header.offsetParent !== null
            ? header
            : null;
      if (target === null) {
        setOpened(true);
        return;
      }
      target.focus();
      target.select();
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
    };
  }, []);

  const go = () => {
    setOpened(false);
    void navigate(headerSearchAddress(text));
  };

  return (
    <>
      <Box visibleFrom="sm" style={{ display: 'flex', flex: '0 1 16rem', minWidth: '8rem' }}>
        <SearchForm
          text={text}
          onText={setText}
          onSearch={go}
          inputRef={inputRef}
          inputId={HEADER_SEARCH_ID}
        />
      </Box>
      <Box hiddenFrom="sm">
        <Popover opened={opened} onChange={setOpened} trapFocus position="bottom-end" shadow="md">
          <Popover.Target>
            <ActionIcon
              variant="default"
              size="lg"
              aria-label="Search Songs"
              aria-expanded={opened}
              onClick={() => {
                setOpened((previous) => !previous);
              }}
            >
              <SearchIcon />
            </ActionIcon>
          </Popover.Target>
          <Popover.Dropdown>
            <SearchForm text={text} onText={setText} onSearch={go} />
          </Popover.Dropdown>
        </Popover>
      </Box>
    </>
  );
}
