// Shared, side-effect-free logic: geo lookup with fallbacks, parsing, formatting, settings.
// Mirrors the desktop apps (ipwho.is first, ipapi.co as fallback) and adds the Netlane site as a last resort.

export const PRIMARY_URL = "https://ipwho.is/";
export const FALLBACK_URL = "https://ipapi.co/json/";
export const NETLANE_URL = "https://netlane.vercel.app/api/geo";

export const DEFAULT_SETTINGS = Object.freeze({
  theme: "system", // system | light | dark
  unit: "bits", // bits (Mb/s) | bytes (MB/s)
  notifyOnChange: true,
  showBadge: false,
});

export function isCountryCode(code) {
  return typeof code === "string" && /^[A-Za-z]{2}$/.test(code) && code.toUpperCase() !== "XX";
}

export function flagPath(code) {
  return isCountryCode(code) ? `flags/${code.toLowerCase()}.png` : null;
}

function geo({ ip, country, countryCode, city = "", isp = "", asn = "", timezone = "", source }) {
  if (!ip || !isCountryCode(countryCode)) throw new Error("Missing ip/country");
  const code = countryCode.toUpperCase();
  return { ip, country: country || code, countryCode: code, city, isp, asn, timezone, source, fetchedAt: Date.now() };
}

export function parseIpwhois(json) {
  if (json.success === false) throw new Error(json.message || "ipwho.is reported failure");
  return geo({
    ip: json.ip,
    country: json.country,
    countryCode: json.country_code,
    city: json.city || "",
    isp: json.connection?.isp || json.connection?.org || "",
    asn: json.connection?.asn ? `AS${json.connection.asn}` : "",
    timezone: json.timezone?.id || "",
    source: "ipwho.is",
  });
}

export function parseIpapi(json) {
  if (json.error) throw new Error(json.reason || "ipapi.co reported failure");
  return geo({
    ip: json.ip,
    country: json.country_name,
    countryCode: json.country_code,
    city: json.city || "",
    isp: json.org || "",
    asn: json.asn || "",
    timezone: json.timezone || "",
    source: "ipapi.co",
  });
}

export function parseNetlane(json) {
  let country = json.country;
  try { country = new Intl.DisplayNames(["en"], { type: "region" }).of(json.country) || country; } catch {}
  return geo({
    ip: json.ip,
    country,
    countryCode: json.country,
    city: json.city || json.region || "",
    timezone: json.timezone || "",
    source: "netlane",
  });
}

async function fetchJson(url, timeoutMs = 8000) {
  const response = await fetch(url, { cache: "no-store", signal: AbortSignal.timeout(timeoutMs) });
  if (!response.ok) throw new Error(`HTTP ${response.status} from ${new URL(url).host}`);
  return response.json();
}

/** Tries each provider in order; `fetcher` is injectable for tests. */
export async function lookupGeo(fetcher = fetchJson) {
  const providers = [
    [PRIMARY_URL, parseIpwhois],
    [FALLBACK_URL, parseIpapi],
    [NETLANE_URL, parseNetlane],
  ];
  const errors = [];
  for (const [url, parse] of providers) {
    try {
      return parse(await fetcher(url));
    } catch (e) {
      errors.push(`${new URL(url).host}: ${e.message}`);
    }
  }
  throw new Error("Lookup failed: " + errors.join("; "));
}

export function mbps(bytes, seconds) {
  return seconds > 0 && bytes > 0 ? (bytes * 8) / seconds / 1e6 : 0;
}

export function formatSpeed(mbpsValue, unit) {
  return unit === "bytes" ? `${(mbpsValue / 8).toFixed(1)} MB/s` : `${mbpsValue.toFixed(1)} Mbps`;
}
