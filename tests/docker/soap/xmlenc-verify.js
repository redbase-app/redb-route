// Independent XML-Encryption validator (Node built-in crypto = OpenSSL, zero shared code with the connector).
// Decrypts a redb.Route.Soap WSS-encrypted envelope with the recipient RSA private key (PEM):
//   CipherValue[0] = RSA-OAEP-wrapped AES session key (EncryptedKey, in wsse:Security)
//   CipherValue[1] = AES-256-CBC ciphertext of the body (EncryptedData, in soap:Body; IV = first 16 bytes)
// XML-Encryption mandates its own CBC padding (arbitrary bytes, LAST byte = pad length) — not PKCS7 — so
// auto-padding is disabled and the padding is stripped by the trailing length byte.
const fs = require('fs');
const crypto = require('crypto');

const xmlPath = process.env.ENC_XML || '/app/enc-envelope.xml';
const pemPath = process.env.ENC_PEM || '/app/enc-key.pem';
const xml = fs.readFileSync(xmlPath, 'utf8');
const pem = fs.readFileSync(pemPath, 'utf8');

const re = /<(?:\w+:)?CipherValue>([\s\S]*?)<\/(?:\w+:)?CipherValue>/g;
const blobs = [];
let m;
while ((m = re.exec(xml)) !== null) blobs.push(Buffer.from(m[1].replace(/\s+/g, ''), 'base64'));
if (blobs.length < 2) { console.error('NEED_TWO_CIPHERVALUES got ' + blobs.length); process.exit(2); }

let sessionKey;
try {
  sessionKey = crypto.privateDecrypt(
    { key: pem, padding: crypto.constants.RSA_PKCS1_OAEP_PADDING, oaepHash: 'sha1' }, blobs[0]);
} catch (e) { console.error('RSA_UNWRAP_FAIL ' + e.message); process.exit(3); }

try {
  const iv = blobs[1].subarray(0, 16);
  const data = blobs[1].subarray(16);
  const d = crypto.createDecipheriv('aes-256-cbc', sessionKey, iv);
  d.setAutoPadding(false);
  const raw = Buffer.concat([d.update(data), d.final()]);
  const padLen = raw[raw.length - 1];                 // XML-Enc: last byte = number of padding bytes
  const plain = raw.subarray(0, raw.length - padLen).toString('utf8');
  console.log('PLAIN:' + plain);
} catch (e) { console.error('AES_FAIL ' + e.message); process.exit(4); }
