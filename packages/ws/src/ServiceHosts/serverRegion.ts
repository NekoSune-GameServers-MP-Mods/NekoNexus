/** Adds the ISO country token consumed by the Unity client's existing flag loader. */
export function withServerRegionFlag<T extends { Name?: string; CountryCode?: string | null; Region?: number }>(server: T): T {
  const fallbackCodes: Record<number, string> = { 0: 'US', 1: 'EU', 2: 'AS', 3: 'US', 4: 'KR', 5: 'JP' };
  const countryCode = typeof server.CountryCode === 'string' && /^[A-Z]{2}$/.test(server.CountryCode)
    ? server.CountryCode
    : fallbackCodes[Number(server.Region)];
  if (!countryCode) return server;
  const name = String(server.Name ?? 'Server').replace(/^\[[^\]]+\]\s*/, '');
  return { ...server, Name: `[${countryCode}] ${name}` };
}
