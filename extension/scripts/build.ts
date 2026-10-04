import { buildExtension, distDirectory } from './lib/build.ts';

const version = await buildExtension();
console.log(`Built n8Tracks extension ${version.full} into ${distDirectory}`);
