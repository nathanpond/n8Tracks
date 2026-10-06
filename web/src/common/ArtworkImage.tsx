import type { CSSProperties } from 'react';
import { artworkAlt, type Artwork } from '../api/artwork';

/** The thumbnail sizes n8Tracks makes, in pixels on the long side. */
export type ArtworkSize = '96' | '320' | '1024';

/**
 * An owner's artwork as a square of `pixels` CSS pixels, from the `size` thumbnail, cropped to the
 * centre (a crop set on the artwork is applied by a later story). With no artwork, one neutral
 * placeholder, the same for every owner, named "No artwork".
 */
export function ArtworkImage({
  artwork,
  title,
  size,
  pixels,
}: {
  artwork: Artwork | null;
  /** The owner's title or name, for the text alternative. */
  title: string;
  size: ArtworkSize;
  pixels: number;
}) {
  const box: CSSProperties = {
    width: pixels,
    height: pixels,
    flex: `0 0 ${String(pixels)}px`,
    borderRadius: 'var(--mantine-radius-sm)',
    display: 'block',
  };

  if (artwork === null) {
    return (
      <div
        role="img"
        aria-label="No artwork"
        data-artwork="none"
        style={{
          ...box,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          background: 'var(--mantine-color-default-hover)',
          border: '1px solid var(--mantine-color-default-border)',
          color: 'var(--mantine-color-dimmed)',
        }}
      >
        <svg
          aria-hidden="true"
          viewBox="0 0 24 24"
          width={Math.max(12, Math.round(pixels * 0.4))}
          height={Math.max(12, Math.round(pixels * 0.4))}
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <path d="M9 18V5l12-2v13" />
          <circle cx="6" cy="18" r="3" />
          <circle cx="18" cy="16" r="3" />
        </svg>
      </div>
    );
  }

  return (
    <img
      src={artwork.urls[size]}
      alt={artworkAlt(title)}
      data-artwork={artwork.assetId}
      width={pixels}
      height={pixels}
      loading="lazy"
      style={{ ...box, objectFit: 'cover' }}
    />
  );
}
