import { inflateRawSync } from 'node:zlib';

/**
 * Reads the entries of a ZIP archive from its central directory: enough to check what a downloaded
 * backup holds without a ZIP dependency. Handles the stored and deflated methods, which are all a
 * backup uses; anything else (ZIP64, encryption) throws.
 */
export function readZip(archive: Buffer): Map<string, Buffer> {
  const endOfCentralDirectory = archive.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  if (endOfCentralDirectory < 0) {
    throw new Error('Not a ZIP archive: no end of central directory.');
  }
  const count = archive.readUInt16LE(endOfCentralDirectory + 10);
  let offset = archive.readUInt32LE(endOfCentralDirectory + 16);

  const entries = new Map<string, Buffer>();
  for (let index = 0; index < count; index++) {
    if (archive.readUInt32LE(offset) !== 0x02014b50) {
      throw new Error(`Entry ${String(index)} is not a central directory header.`);
    }
    const method = archive.readUInt16LE(offset + 10);
    const compressedSize = archive.readUInt32LE(offset + 20);
    const nameLength = archive.readUInt16LE(offset + 28);
    const extraLength = archive.readUInt16LE(offset + 30);
    const commentLength = archive.readUInt16LE(offset + 32);
    const localHeader = archive.readUInt32LE(offset + 42);
    const name = archive.toString('utf8', offset + 46, offset + 46 + nameLength);

    const dataStart =
      localHeader +
      30 +
      archive.readUInt16LE(localHeader + 26) +
      archive.readUInt16LE(localHeader + 28);
    const data = archive.subarray(dataStart, dataStart + compressedSize);
    if (method === 0) {
      entries.set(name, Buffer.from(data));
    } else if (method === 8) {
      entries.set(name, inflateRawSync(data));
    } else {
      throw new Error(`Entry ${name} uses compression method ${String(method)}.`);
    }

    offset += 46 + nameLength + extraLength + commentLength;
  }
  return entries;
}
