import {
  createTheme,
  localStorageColorSchemeManager,
  type CSSVariablesResolver,
} from '@mantine/core';
import { knownStatuses, palette, type SchemePalette } from './palette';

export const COLOR_SCHEME_STORAGE_KEY = 'n8tracks-color-scheme';

export const colorSchemeManager = localStorageColorSchemeManager({
  key: COLOR_SCHEME_STORAGE_KEY,
});

export const theme = createTheme({});

function schemeVariables(scheme: SchemePalette): Record<string, string> {
  const variables: Record<string, string> = {
    '--mantine-color-body': scheme.body.background,
    '--mantine-color-text': scheme.body.text,
    '--n8-color-secondary-text': scheme.secondaryText,
    '--n8-notice-background': scheme.notice.background,
    '--n8-notice-text': scheme.notice.text,
    '--n8-notice-border': scheme.notice.border,
  };

  for (const status of knownStatuses) {
    variables[`--n8-status-${status}-background`] = scheme.status[status].background;
    variables[`--n8-status-${status}-text`] = scheme.status[status].text;
  }

  return variables;
}

/** Puts the palette on the page as CSS variables, one set per colour scheme. */
export const cssVariablesResolver: CSSVariablesResolver = () => ({
  variables: {},
  light: schemeVariables(palette.light),
  dark: schemeVariables(palette.dark),
});
