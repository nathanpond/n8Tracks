import {
  createTheme,
  InputWrapper,
  Stepper,
  localStorageColorSchemeManager,
  type CSSVariablesResolver,
} from '@mantine/core';
import { knownStatuses, palette, type SchemePalette } from './palette';

export const COLOR_SCHEME_STORAGE_KEY = 'n8tracks-color-scheme';

export const colorSchemeManager = localStorageColorSchemeManager({
  key: COLOR_SCHEME_STORAGE_KEY,
});

/**
 * The shade filled controls (a primary button) are drawn in. Mantine's default light shade (6) puts
 * white text on a blue below 4.5:1; shade 8 is above it in both schemes.
 */
export const PRIMARY_SHADE = 8;

/** Secondary text: Mantine's dimmed grey is below 4.5:1 on the page, this colour is above it. */
const secondaryText = { color: 'var(--n8-color-secondary-text)' };

export const theme = createTheme({
  primaryShade: { light: PRIMARY_SHADE, dark: PRIMARY_SHADE },
  components: {
    InputWrapper: InputWrapper.extend({ styles: { description: secondaryText } }),
    Stepper: Stepper.extend({ styles: { stepDescription: secondaryText } }),
  },
});

function schemeVariables(scheme: SchemePalette): Record<string, string> {
  const variables: Record<string, string> = {
    '--mantine-color-body': scheme.body.background,
    '--mantine-color-text': scheme.body.text,
    '--n8-color-secondary-text': scheme.secondaryText,
    // Mantine draws field errors (message, text, and border of an invalid field) in this colour.
    '--mantine-color-error': scheme.errorText,
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
