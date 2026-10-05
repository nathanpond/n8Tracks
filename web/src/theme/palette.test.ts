import { describe, expect, it } from 'vitest';
import { contrastRatio, knownStatuses, palette, stateColours } from './palette';
import { cssVariablesResolver, PRIMARY_SHADE, theme } from './theme';
import { DEFAULT_THEME } from '@mantine/core';

// WCAG 2.1 AA: 4.5:1 for text (1.4.3), 3:1 for the edge of a graphical object (1.4.11).
const TEXT = 4.5;
const NON_TEXT = 3;

describe('contrastRatio', () => {
  it('matches the WCAG reference values', () => {
    expect(contrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrastRatio('#ffffff', '#ffffff')).toBe(1);
    // The well-known boundary grey: #767676 on white is just over 4.5.
    expect(contrastRatio('#767676', '#ffffff')).toBeCloseTo(4.54, 2);
    expect(contrastRatio('#777777', '#ffffff')).toBeLessThan(TEXT);
  });
});

describe('filled controls', () => {
  it('have readable white labels on the primary colour', () => {
    const primary = DEFAULT_THEME.colors[theme.primaryColor ?? DEFAULT_THEME.primaryColor];
    expect(theme.primaryShade).toEqual({ light: PRIMARY_SHADE, dark: PRIMARY_SHADE });
    expect(contrastRatio('#ffffff', primary?.[PRIMARY_SHADE] ?? '')).toBeGreaterThanOrEqual(TEXT);
  });
});

describe.each(['light', 'dark'] as const)('the %s scheme', (schemeName) => {
  const scheme = palette[schemeName];

  it('has readable body text', () => {
    expect(contrastRatio(scheme.body.text, scheme.body.background)).toBeGreaterThanOrEqual(TEXT);
  });

  it('has readable secondary text', () => {
    expect(contrastRatio(scheme.secondaryText, scheme.body.background)).toBeGreaterThanOrEqual(
      TEXT,
    );
  });

  it('has readable field errors', () => {
    expect(contrastRatio(scheme.errorText, scheme.body.background)).toBeGreaterThanOrEqual(TEXT);
  });

  it('has a readable out-of-date notice that stands out from the page', () => {
    expect(contrastRatio(scheme.notice.text, scheme.notice.background)).toBeGreaterThanOrEqual(
      TEXT,
    );
    expect(contrastRatio(scheme.notice.border, scheme.body.background)).toBeGreaterThanOrEqual(
      NON_TEXT,
    );
  });

  it.each(knownStatuses)('has a readable %s badge that stands out from the page', (status) => {
    const badge = scheme.status[status];

    expect(contrastRatio(badge.text, badge.background)).toBeGreaterThanOrEqual(TEXT);
    expect(contrastRatio(badge.background, scheme.body.background)).toBeGreaterThanOrEqual(
      NON_TEXT,
    );
  });

  it.each(Object.keys(stateColours) as (keyof typeof stateColours)[])(
    'draws a %s state as readable text on the page',
    (name) => {
      expect(
        contrastRatio(stateColours[name][schemeName], scheme.body.background),
      ).toBeGreaterThanOrEqual(TEXT);
    },
  );

  it('is what the theme puts on the page', () => {
    const variables = cssVariablesResolver(DEFAULT_THEME)[schemeName];

    expect(variables['--mantine-color-body']).toBe(scheme.body.background);
    expect(variables['--mantine-color-text']).toBe(scheme.body.text);
    expect(variables['--n8-color-secondary-text']).toBe(scheme.secondaryText);
    expect(variables['--mantine-color-error']).toBe(scheme.errorText);
    expect(variables['--n8-notice-background']).toBe(scheme.notice.background);
    expect(variables['--n8-notice-text']).toBe(scheme.notice.text);
    for (const status of knownStatuses) {
      expect(variables[`--n8-status-${status}-background`]).toBe(scheme.status[status].background);
      expect(variables[`--n8-status-${status}-text`]).toBe(scheme.status[status].text);
    }
    for (const [name, colour] of Object.entries(stateColours)) {
      expect(variables[`--n8-state-${name}`]).toBe(colour[schemeName]);
    }
  });
});
