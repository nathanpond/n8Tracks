/**
 * Every colour pair the shell puts text on, per colour scheme. The theme turns these into CSS
 * variables, and the contrast test checks each pair against WCAG 2.1 AA, so a colour changed here
 * is checked before it ships.
 */
export const knownStatuses = ['healthy', 'degraded', 'unhealthy'] as const;

export type KnownStatus = (typeof knownStatuses)[number];

export interface ColorPair {
  background: string;
  text: string;
}

export interface SchemePalette {
  /** Page background and body text. */
  body: ColorPair;
  /** Less prominent text (details, captions) on the page background. */
  secondaryText: string;
  /** The out-of-date notice. */
  notice: ColorPair & { border: string };
  /** Status badges. */
  status: Record<KnownStatus, ColorPair>;
}

export const palette: Record<'light' | 'dark', SchemePalette> = {
  light: {
    body: { background: '#ffffff', text: '#1a1b1e' },
    secondaryText: '#495057',
    notice: { background: '#fff3bf', text: '#1a1b1e', border: '#8a5a00' },
    status: {
      healthy: { background: '#1e6b30', text: '#ffffff' },
      degraded: { background: '#8a5a00', text: '#ffffff' },
      unhealthy: { background: '#c92a2a', text: '#ffffff' },
    },
  },
  dark: {
    body: { background: '#1a1b1e', text: '#4a4a4f' },
    secondaryText: '#adb5bd',
    notice: { background: '#3d2c00', text: '#fff3bf', border: '#ffd43b' },
    status: {
      healthy: { background: '#8ce99a', text: '#0b2e13' },
      degraded: { background: '#ffd43b', text: '#3d2c00' },
      unhealthy: { background: '#ffa8a8', text: '#4a0b0b' },
    },
  },
};

export function isKnownStatus(status: string): status is KnownStatus {
  return (knownStatuses as readonly string[]).includes(status);
}

function channel(value: number): number {
  const scaled = value / 255;
  return scaled <= 0.04045 ? scaled / 12.92 : ((scaled + 0.055) / 1.055) ** 2.4;
}

/** WCAG 2.1 relative luminance of a `#rrggbb` colour. */
export function relativeLuminance(hex: string): number {
  const match = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex);
  if (!match) {
    throw new Error(`Not a #rrggbb colour: ${hex}`);
  }

  const [red, green, blue] = match.slice(1).map((part) => channel(parseInt(part, 16)));
  return 0.2126 * (red ?? 0) + 0.7152 * (green ?? 0) + 0.0722 * (blue ?? 0);
}

/** WCAG 2.1 contrast ratio between two `#rrggbb` colours, from 1 to 21. */
export function contrastRatio(first: string, second: string): number {
  const lighter = Math.max(relativeLuminance(first), relativeLuminance(second));
  const darker = Math.min(relativeLuminance(first), relativeLuminance(second));
  return (lighter + 0.05) / (darker + 0.05);
}
