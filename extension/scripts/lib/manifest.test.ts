import { describe, expect, it } from 'vitest';
import {
  builtPath,
  referencedFiles,
  transformManifest,
  validateManifest,
  type Manifest,
} from './manifest.ts';
import { parseProductVersion } from './version.ts';

const icons = { '16': 'icons/icon-16.png', '48': 'icons/icon-48.png', '128': 'icons/icon-128.png' };

function validManifest(): Manifest {
  return {
    manifest_version: 3,
    name: 'n8Tracks',
    version: '0.1.0',
    description: 'Connects n8Tracks to your Suno session.',
    icons: { ...icons },
    action: { default_title: 'n8Tracks', default_popup: 'popup/popup.html', default_icon: icons },
    background: { service_worker: 'service-worker.js', type: 'module' },
    permissions: ['storage', 'scripting', 'tabs', 'alarms', 'downloads'],
    optional_host_permissions: ['https://suno.com/*', 'https://*/*', 'http://*/*'],
    options_ui: { page: 'options/options.html', open_in_tab: true },
  };
}

const sourceIcons = {
  '16': 'public/icons/icon-16.png',
  '48': 'public/icons/icon-48.png',
  '128': 'public/icons/icon-128.png',
};

function sourceManifest(): Manifest {
  return {
    ...validManifest(),
    version: '0.0.0',
    icons: sourceIcons,
    action: {
      default_title: 'n8Tracks',
      default_popup: 'src/popup/popup.html',
      default_icon: sourceIcons,
    },
    background: { service_worker: 'src/background/service-worker.ts', type: 'module' },
    options_ui: { page: 'src/options/options.html', open_in_tab: true },
  };
}

describe('validateManifest', () => {
  it('accepts the manifest this extension is allowed', () => {
    expect(validateManifest(validManifest())).toEqual([]);
  });

  it('accepts a pre-release manifest with a version name', () => {
    expect(validateManifest({ ...validManifest(), version_name: '0.1.0-edge.abc1234' })).toEqual(
      [],
    );
  });

  const bad: [string, Manifest, string][] = [
    [
      'an additional permission',
      { permissions: ['storage', 'scripting', 'tabs', 'alarms', 'downloads', 'webRequest'] },
      'permissions',
    ],
    [
      'the permissions without downloads',
      { permissions: ['storage', 'scripting', 'tabs', 'alarms'] },
      'permissions',
    ],
    [
      'a cookies permission',
      { permissions: ['storage', 'scripting', 'tabs', 'cookies'] },
      'permissions',
    ],
    ['a missing permission', { permissions: ['storage', 'tabs'] }, 'permissions'],
    ['only the M1 permission', { permissions: ['storage'] }, 'permissions'],
    ['a different permission', { permissions: ['cookies'] }, 'permissions'],
    ['no permissions', { permissions: undefined }, 'permissions'],
    ['a host permission', { host_permissions: ['https://suno.com/*'] }, 'host_permissions'],
    ['an empty host permission list', { host_permissions: [] }, 'host_permissions'],
    [
      'an additional optional host',
      {
        optional_host_permissions: [
          'https://suno.com/*',
          'https://*/*',
          'http://*/*',
          'https://example.com/*',
        ],
      },
      'optional_host_permissions',
    ],
    [
      'a wider optional host',
      { optional_host_permissions: ['https://*.suno.com/*', 'https://*/*', 'http://*/*'] },
      'optional_host_permissions',
    ],
    [
      'the M1 optional host alone',
      { optional_host_permissions: ['https://suno.com/*'] },
      'optional_host_permissions',
    ],
    [
      'any scheme',
      { optional_host_permissions: ['https://suno.com/*', '*://*/*'] },
      'optional_host_permissions',
    ],
    ['every host', { optional_host_permissions: ['<all_urls>'] }, 'optional_host_permissions'],
    ['no optional host', { optional_host_permissions: [] }, 'optional_host_permissions'],
    ['an optional permission', { optional_permissions: ['tabs'] }, 'optional_permissions'],
    [
      'a content script',
      { content_scripts: [{ matches: ['https://suno.com/*'], js: ['content.js'] }] },
      'content_scripts',
    ],
    [
      'external connections',
      { externally_connectable: { matches: ['https://suno.com/*'] } },
      'externally_connectable',
    ],
    [
      'resources open to every page',
      { web_accessible_resources: [{ resources: ['relay.js'], matches: ['<all_urls>'] }] },
      'web_accessible_resources',
    ],
    [
      'a looser content security policy',
      { content_security_policy: { extension_pages: "script-src 'self' 'unsafe-eval'" } },
      'content_security_policy',
    ],
    [
      'network request rules',
      { declarative_net_request: { rule_resources: [] } },
      'declarative_net_request',
    ],
    [
      'a replaced browser page',
      { chrome_url_overrides: { newtab: 'new.html' } },
      'chrome_url_overrides',
    ],
    ['an unknown key', { side_panel: { default_path: 'panel.html' } }, 'side_panel'],
    ['Manifest V2', { manifest_version: 2 }, 'manifest_version'],
    ['another name', { name: 'Something else' }, 'name'],
    ['the placeholder version', { version: '0.0.0' }, 'version'],
    ['a version with a suffix', { version: '0.1.0-edge.1' }, 'version'],
    ['a version name for another version', { version_name: '0.2.0-edge.1' }, 'version_name'],
    ['a classic service worker', { background: { service_worker: 'sw.js' } }, 'background'],
    ['no popup', { action: { default_icon: icons } }, 'action.default_popup'],
    ['a missing icon size', { icons: { '16': 'icons/icon-16.png' } }, 'icons'],
    ['no options page', { options_ui: undefined }, 'options_ui'],
    [
      'an options page in the popup frame',
      { options_ui: { page: 'options/options.html', open_in_tab: false } },
      'options_ui',
    ],
  ];

  it.each(bad)('rejects %s', (_, change, field) => {
    const problems = validateManifest({ ...validManifest(), ...change });

    expect(problems).toHaveLength(1);
    expect(problems[0]).toMatch(new RegExp(`^${field.replace('.', '\\.')}\\b`));
  });

  it('reports every problem, not only the first', () => {
    const problems = validateManifest({
      ...validManifest(),
      permissions: ['storage', 'scripting', 'tabs', 'alarms', 'downloads', 'cookies'],
      host_permissions: ['<all_urls>'],
    });

    expect(problems).toHaveLength(2);
  });

  it.each([null, [], 'manifest'])('rejects %j, which is not an object', (value) => {
    expect(validateManifest(value)).toEqual(['The manifest must be a JSON object.']);
  });
});

describe('transformManifest', () => {
  it('fills in a release version without a version name', () => {
    const built = transformManifest(sourceManifest(), parseProductVersion('0.1.0', 'VERSION'));

    expect(built.version).toBe('0.1.0');
    expect(built).not.toHaveProperty('version_name');
  });

  it('gives a pre-release build the numeric version and the full version name', () => {
    const built = transformManifest(
      sourceManifest(),
      parseProductVersion('0.1.0-edge.abc1234', 'VERSION'),
    );

    expect(built.version).toBe('0.1.0');
    expect(built.version_name).toBe('0.1.0-edge.abc1234');
  });

  it('turns source paths into built paths and changes nothing else', () => {
    const built = transformManifest(sourceManifest(), parseProductVersion('0.1.0', 'VERSION'));

    expect(built).toEqual(validManifest());
  });

  it('produces a manifest that passes validation', () => {
    const built = transformManifest(sourceManifest(), parseProductVersion('0.1.0-rc.1', 'VERSION'));

    expect(validateManifest(built)).toEqual([]);
  });

  it('does not let a version name in the source leak into a release build', () => {
    const built = transformManifest(
      { ...sourceManifest(), version_name: 'stale' },
      parseProductVersion('0.1.0', 'VERSION'),
    );

    expect(built).not.toHaveProperty('version_name');
  });

  it('refuses a path outside src/ and public/', () => {
    expect(() => builtPath('../secrets.txt')).toThrow('not under src/ or public/');
  });
});

describe('referencedFiles', () => {
  it('lists the service worker, the popup, the options page, and each icon once', () => {
    expect(referencedFiles(validManifest()).toSorted()).toEqual([
      'icons/icon-128.png',
      'icons/icon-16.png',
      'icons/icon-48.png',
      'options/options.html',
      'popup/popup.html',
      'service-worker.js',
    ]);
  });
});
