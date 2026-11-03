const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
const crypto = require('node:crypto');

const project = path.resolve(__dirname, '..');
const archivePath = path.resolve(process.argv[3] ?? path.join(project, 'dist', 'AltSync-win-x64.zip'));
const archive = fs.readFileSync(archivePath);
const vaultPath = process.argv[2];
if (!vaultPath) throw new Error('Pass the local vault path. Values are checked in memory and are never printed.');
const vault = JSON.parse(fs.readFileSync(vaultPath, 'utf8'));
const needles = [];
for (const account of vault.accounts ?? []) {
  for (const [category, value] of [['accountId', String(account.id ?? '')], ['username', account.username], ['encryptedSession', account.secret]]) {
    if (typeof value !== 'string' || value.length < 5) continue;
    for (const encoding of ['utf8', 'utf16le']) needles.push({ category, bytes: Buffer.from(value, encoding) });
  }
}
let footer = -1;
for (let at = archive.length - 22; at >= Math.max(0, archive.length - 65557); at--) {
  if (archive.readUInt32LE(at) === 0x06054b50) { footer = at; break; }
}
if (footer < 0) throw new Error('ZIP footer missing.');
const count = archive.readUInt16LE(footer + 10);
let position = archive.readUInt32LE(footer + 16);
const matches = [], sensitivePaths = [];
let total = 0;
for (let index = 0; index < count; index++) {
  if (archive.readUInt32LE(position) !== 0x02014b50) throw new Error('Invalid central directory.');
  const method = archive.readUInt16LE(position + 10), compressed = archive.readUInt32LE(position + 20), expected = archive.readUInt32LE(position + 24);
  const nameLength = archive.readUInt16LE(position + 28), extraLength = archive.readUInt16LE(position + 30), commentLength = archive.readUInt16LE(position + 32), offset = archive.readUInt32LE(position + 42);
  const name = archive.toString('utf8', position + 46, position + 46 + nameLength);
  position += 46 + nameLength + extraLength + commentLength;
  if (/(^|\/)(accounts\.json|library\.json|\.env|state\.json|cookies(?:-journal)?|local state|login data|webview2|backups|transcripts|local storage|session storage|indexeddb|\.git)(\/|$)|\.altsync$/i.test(name)) sensitivePaths.push(name);
  if (expected > 200 * 1024 * 1024 || total + expected > 1024 * 1024 * 1024) throw new Error('Archive exceeds audit limits.');
  if (archive.readUInt32LE(offset) !== 0x04034b50) throw new Error('Invalid ZIP local header.');
  const start = offset + 30 + archive.readUInt16LE(offset + 26) + archive.readUInt16LE(offset + 28);
  const packed = archive.subarray(start, start + compressed);
  const content = method === 0 ? packed : method === 8 ? zlib.inflateRawSync(packed, { maxOutputLength: Math.max(expected, 1) }) : null;
  if (!content || content.length !== expected) throw new Error('Archive entry could not be fully audited.');
  total += expected;
  const categories = [...new Set(needles.filter(needle => content.indexOf(needle.bytes) !== -1).map(needle => needle.category))];
  if (categories.length) matches.push({ file: name, categories });
}
const report = {
  archive: path.basename(archivePath),
  sha256: crypto.createHash('sha256').update(archive).digest('hex'),
  filesChecked: count, bytesChecked: total, localAccountCount: vault.accounts?.length ?? 0,
  sensitivePaths, matchingFiles: matches,
  accountValuesPrinted: false, sessionCookiesDecrypted: false,
};
fs.writeFileSync(path.join(project, 'artifacts', 'publication-content-audit.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
if (sensitivePaths.length || matches.length) process.exitCode = 1;
