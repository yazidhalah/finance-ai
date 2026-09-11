import { createHmac } from 'node:crypto'

/** RFC 6238, the same parameters the API uses (SHA-1, 30 s, 6 digits) — enough to act as the "authenticator app" in a journey. */
export function totp(secretBase32: string, at: number = Date.now()): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
  let bits = 0, buffer = 0
  const bytes: number[] = []
  for (const c of secretBase32.toUpperCase().replace(/=+$/, '')) {
    buffer = (buffer << 5) | alphabet.indexOf(c)
    bits += 5
    if (bits >= 8) { bytes.push((buffer >> (bits - 8)) & 0xff); bits -= 8 }
  }
  const counter = Buffer.alloc(8)
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 1000 / 30)))
  const hash = createHmac('sha1', Buffer.from(bytes)).update(counter).digest()
  const offset = hash[hash.length - 1]! & 0x0f
  const code = ((hash[offset]! & 0x7f) << 24) | (hash[offset + 1]! << 16) | (hash[offset + 2]! << 8) | hash[offset + 3]!
  return String(code % 1_000_000).padStart(6, '0')
}
