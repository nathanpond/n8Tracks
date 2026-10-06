import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { distDirectory, extensionRoot, productVersion } from '../scripts/lib/build.ts';
import {
  referencedFiles,
  relayOutput,
  validateManifest,
  type Manifest,
} from '../scripts/lib/manifest.ts';

// The global setup has just built dist/; these tests read what the build really wrote.
const manifest = JSON.parse(readFileSync(join(distDirectory, 'manifest.json'), 'utf8')) as Manifest;

describe('the built manifest', () => {
  it('is Manifest V3 and asks only for storage, scripting, and tabs', () => {
    expect(manifest.manifest_version).toBe(3);
    expect(manifest.permissions).toEqual(['storage', 'scripting', 'tabs']);
    expect(manifest).not.toHaveProperty('optional_permissions');
  });

  it('holds no host access of its own: hosts are optional, granted one origin at a time', () => {
    expect(manifest).not.toHaveProperty('host_permissions');
    expect(manifest.optional_host_permissions).toEqual([
      'https://suno.com/*',
      'https://*/*',
      'http://*/*',
    ]);
    // Content scripts are registered at pairing for the paired origin, never declared.
    expect(manifest).not.toHaveProperty('content_scripts');
    expect(manifest).not.toHaveProperty('externally_connectable');
  });

  it('fails validation with host_permissions or any permission beyond storage, scripting, and tabs', () => {
    expect(validateManifest({ ...manifest, host_permissions: ['https://suno.com/*'] })).toEqual([
      'host_permissions must be absent.',
    ]);
    for (const extra of ['downloads', 'cookies', 'webRequest', 'activeTab']) {
      expect(
        validateManifest({
          ...manifest,
          permissions: [...(manifest.permissions as string[]), extra],
        }),
        extra,
      ).toHaveLength(1);
    }
  });

  it('passes validation', () => {
    expect(validateManifest(manifest)).toEqual([]);
  });

  it('is named n8Tracks and carries the product version', () => {
    const version = productVersion();

    expect(manifest.name).toBe('n8Tracks');
    expect(manifest.description).toBe('Connects n8Tracks to your Suno session.');
    expect(manifest.version).toBe(version.numeric);
    expect(manifest.version_name).toBe(version.isPreRelease ? version.full : undefined);
  });

  it('names a service worker, a popup, an options page, and icons that the build made', () => {
    const files = referencedFiles(manifest);

    expect(files).toContain('service-worker.js');
    expect(files).toContain('popup/popup.html');
    expect(files).toContain('options/options.html');
    expect(files).toHaveLength(6);
    for (const file of files) {
      expect(existsSync(join(distDirectory, file)), file).toBe(true);
    }
  });
});

describe('the built popup', () => {
  const popupDirectory = join(distDirectory, 'popup');
  const html = readFileSync(join(popupDirectory, 'popup.html'), 'utf8');

  it('loads its script and stylesheet from files in dist, by relative URL', () => {
    const urls = [...html.matchAll(/(?:src|href)="([^"]+)"/g)].map((match) => match[1] ?? '');

    expect(urls.some((url) => url.endsWith('.js'))).toBe(true);
    expect(urls.some((url) => url.endsWith('.css'))).toBe(true);
    for (const url of urls) {
      expect(url.startsWith('.'), url).toBe(true);
      expect(existsSync(join(popupDirectory, url)), url).toBe(true);
    }
  });

  it('has no inline script, which the extension content security policy would block', () => {
    expect(html).not.toMatch(/<script(?![^>]*\bsrc=)[^>]*>/);
  });

  it('follows the system colour scheme', () => {
    const assets = join(distDirectory, 'assets');
    const css = readdirSync(assets)
      .filter((name) => name.endsWith('.css'))
      .map((name) => readFileSync(join(assets, name), 'utf8'))
      .join('\n');

    expect(css).toMatch(/prefers-color-scheme:\s*dark/);
  });
});

describe('the built relay', () => {
  const relay = readFileSync(join(distDirectory, relayOutput), 'utf8');

  it('is one classic script, as a registered content script must be', () => {
    expect(relay).not.toMatch(/^\s*import[\s{*]/m);
    expect(relay).not.toMatch(/^\s*export\s/m);
    expect(relay).toContain('n8tracks-extension');
  });
});

describe('package.json', () => {
  it('has the version in the VERSION file', () => {
    const versionFile = readFileSync(join(extensionRoot, '..', 'VERSION'), 'utf8').trim();
    const packageJson = JSON.parse(readFileSync(join(extensionRoot, 'package.json'), 'utf8')) as {
      version: string;
    };

    expect(packageJson.version).toBe(versionFile);
  });
});
