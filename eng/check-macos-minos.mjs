#!/usr/bin/env node
// Fails when a macOS Mach-O binary records a minimum OS above the supported
// floor. Reads LC_BUILD_VERSION (or the legacy LC_VERSION_MIN_MACOSX) from
// every slice, so it runs on Linux without otool/vtool.
//
// Usage: node eng/check-macos-minos.mjs --max 15.0 <file-or-directory>...
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const LC_VERSION_MIN_MACOSX = 0x24;
const LC_BUILD_VERSION = 0x32;
const PLATFORM_MACOS = 1;

function usage(message) {
  console.error(message);
  console.error('Usage: node eng/check-macos-minos.mjs --max <major.minor> <file-or-directory>...');
  process.exit(64);
}

function parseVersion(text) {
  const match = /^(\d+)\.(\d+)(?:\.(\d+))?$/.exec(text ?? '');
  if (!match) {
    usage(`Invalid version '${text}'.`);
  }
  return (Number(match[1]) << 16) | (Number(match[2]) << 8) | Number(match[3] ?? 0);
}

function formatVersion(encoded) {
  const patch = encoded & 0xff;
  const text = `${encoded >>> 16}.${(encoded >>> 8) & 0xff}`;
  return patch === 0 ? text : `${text}.${patch}`;
}

function cpuName(cputype) {
  switch (cputype >>> 0) {
    case 0x01000007: return 'x86_64';
    case 0x0100000c: return 'arm64';
    case 0x00000007: return 'i386';
    default: return `cpu 0x${(cputype >>> 0).toString(16)}`;
  }
}

// Returns the slices of a thin or fat Mach-O file, or null for other files.
function sliceOffsets(buffer) {
  if (buffer.length < 8) {
    return null;
  }
  const magicBe = buffer.readUInt32BE(0);
  if (magicBe === 0xcafebabe || magicBe === 0xcafebabf) {
    const is64 = magicBe === 0xcafebabf;
    const count = buffer.readUInt32BE(4);
    const entrySize = is64 ? 32 : 20;
    const slices = [];
    for (let index = 0; index < count; index++) {
      const entry = 8 + index * entrySize;
      const offset = is64 ? Number(buffer.readBigUInt64BE(entry + 8)) : buffer.readUInt32BE(entry + 8);
      slices.push(offset);
    }
    return slices;
  }
  const magicLe = buffer.readUInt32LE(0);
  if (magicLe === 0xfeedface || magicLe === 0xfeedfacf) {
    return [0];
  }
  if (magicBe === 0xfeedface || magicBe === 0xfeedfacf) {
    throw new Error('big-endian Mach-O slices are not supported');
  }
  return null;
}

function readSlice(buffer, base) {
  const magic = buffer.readUInt32LE(base);
  const headerSize = magic === 0xfeedfacf ? 32 : 28;
  const cputype = buffer.readInt32LE(base + 4);
  const ncmds = buffer.readUInt32LE(base + 16);
  const sizeofcmds = buffer.readUInt32LE(base + 20);
  const end = base + headerSize + sizeofcmds;
  if (end > buffer.length) {
    throw new Error('load commands extend past the end of the file');
  }

  let offset = base + headerSize;
  const versions = [];
  for (let index = 0; index < ncmds; index++) {
    const cmd = buffer.readUInt32LE(offset);
    const cmdsize = buffer.readUInt32LE(offset + 4);
    if (cmdsize < 8 || offset + cmdsize > end) {
      throw new Error(`malformed load command ${index}`);
    }
    if (cmd === LC_BUILD_VERSION) {
      versions.push({
        command: 'LC_BUILD_VERSION',
        platform: buffer.readUInt32LE(offset + 8),
        minos: buffer.readUInt32LE(offset + 12),
        sdk: buffer.readUInt32LE(offset + 16),
      });
    } else if (cmd === LC_VERSION_MIN_MACOSX) {
      versions.push({
        command: 'LC_VERSION_MIN_MACOSX',
        platform: PLATFORM_MACOS,
        minos: buffer.readUInt32LE(offset + 8),
        sdk: buffer.readUInt32LE(offset + 12),
      });
    }
    offset += cmdsize;
  }
  return { arch: cpuName(cputype), versions };
}

function collect(path, files) {
  if (statSync(path).isDirectory()) {
    for (const entry of readdirSync(path).sort()) {
      collect(join(path, entry), files);
    }
  } else {
    files.push(path);
  }
}

const args = process.argv.slice(2);
const maxIndex = args.indexOf('--max');
if (maxIndex < 0) {
  usage('Missing --max.');
}
const maxText = args[maxIndex + 1];
const max = parseVersion(maxText);
const roots = args.filter((_, index) => index !== maxIndex && index !== maxIndex + 1);
if (roots.length === 0) {
  usage('No files or directories were given.');
}

const files = [];
for (const root of roots) {
  collect(root, files);
}

let checked = 0;
const failures = [];
for (const file of files) {
  const buffer = readFileSync(file);
  let slices;
  try {
    slices = sliceOffsets(buffer);
  } catch (error) {
    failures.push(`${file}: ${error.message}`);
    continue;
  }
  if (slices === null) {
    continue;
  }

  for (const base of slices) {
    checked++;
    let slice;
    try {
      slice = readSlice(buffer, base);
    } catch (error) {
      failures.push(`${file}: ${error.message}`);
      continue;
    }
    const macos = slice.versions.filter((version) => version.platform === PLATFORM_MACOS);
    if (macos.length === 0) {
      failures.push(`${file} (${slice.arch}): no macOS LC_BUILD_VERSION or LC_VERSION_MIN_MACOSX load command`);
      continue;
    }
    for (const version of macos) {
      const summary = `${file} (${slice.arch}): ${version.command} minos ${formatVersion(version.minos)} sdk ${formatVersion(version.sdk)}`;
      if (version.minos > max) {
        failures.push(`${summary} is above the supported floor ${maxText}`);
      } else {
        console.log(summary);
      }
    }
  }
}

if (checked === 0) {
  failures.push(`No Mach-O binaries were found under: ${roots.join(', ')}`);
}
if (failures.length > 0) {
  for (const failure of failures) {
    console.error(`error: ${failure}`);
  }
  process.exit(1);
}
console.log(`All ${checked} macOS Mach-O slice(s) require macOS ${maxText} or older.`);
