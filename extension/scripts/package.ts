import { buildExtension, distDirectory, extensionRoot } from './lib/build.ts';
import { packageExtension } from './lib/package.ts';

const version = await buildExtension();
const zipPath = packageExtension(distDirectory, extensionRoot, version.full);
console.log(`Packaged n8Tracks extension ${version.full} as ${zipPath}`);
