/**
 * Test helper (used only by specs, never imported by the app): an unsigned JWT carrying the given claims, enough for
 * the app's read-only look at a token's payload.
 */
export function fakeJwt(claims: Record<string, unknown>): string {
  const b64 = (o: unknown) => btoa(JSON.stringify(o)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${b64({ alg: 'none' })}.${b64(claims)}.signature`;
}
