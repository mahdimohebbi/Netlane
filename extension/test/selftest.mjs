// Run with: node extension/test/selftest.mjs [--live]
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { flagPath, formatSpeed, isCountryCode, lookupGeo, mbps, parseIpapi, parseIpwhois, parseNetlane, FALLBACK_URL, NETLANE_URL, PRIMARY_URL } from "../lib/core.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
let failures = 0;
const check = (ok, name) => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}`);
  if (!ok) failures++;
};
const throws = (fn) => { try { fn(); return false; } catch { return true; } };

const ipwhois = { ip: "203.0.113.42", success: true, country: "Germany", country_code: "DE", city: "Berlin",
  connection: { asn: 3320, org: "Deutsche Telekom AG", isp: "Deutsche Telekom AG" }, timezone: { id: "Europe/Berlin" } };
const ipapi = { ip: "198.51.100.7", city: "Istanbul", country_name: "Turkey", country_code: "TR",
  timezone: "Europe/Istanbul", asn: "AS9121", org: "Turk Telekom" };
const netlane = { ip: "192.0.2.9", country: "IR", region: "23", city: "Tehran", timezone: "Asia/Tehran" };

check(flagPath("US") === "flags/us.png" && flagPath("de") === "flags/de.png", "flag path US/DE");
check(flagPath("XX") === null && flagPath("X1") === null && !isCountryCode(""), "flag path rejects invalid codes");
for (const code of ["us", "de", "ir", "tr", "gb", "jp"]) check(existsSync(join(root, "flags", `${code}.png`)), `flag file ${code}.png exists`);

const a = parseIpwhois(ipwhois);
check(a.ip === "203.0.113.42" && a.countryCode === "DE" && a.country === "Germany" && a.city === "Berlin"
  && a.isp === "Deutsche Telekom AG" && a.asn === "AS3320" && a.timezone === "Europe/Berlin", "parse ipwho.is");
check(throws(() => parseIpwhois({ success: false, message: "Reserved range" })), "ipwho.is success=false rejected");
const b = parseIpapi(ipapi);
check(b.countryCode === "TR" && b.country === "Turkey" && b.isp === "Turk Telekom" && b.asn === "AS9121", "parse ipapi.co");
check(throws(() => parseIpapi({ error: true, reason: "RateLimited" })), "ipapi.co error rejected");
const c = parseNetlane(netlane);
check(c.countryCode === "IR" && c.country === "Iran" && c.city === "Tehran" && c.timezone === "Asia/Tehran", "parse netlane /api/geo");

check(Math.abs(mbps(25_000_000, 2) - 100) < 1e-9 && mbps(100, 0) === 0, "Mbps calculation");
check(formatSpeed(80, "bits") === "80.0 Mbps" && formatSpeed(80, "bytes") === "10.0 MB/s", "speed formatting");

const fallback = await lookupGeo(async (url) => { if (url === PRIMARY_URL) throw new Error("timeout"); return ipapi; });
check(fallback.countryCode === "TR", "falls back to ipapi.co when ipwho.is fails");
const last = await lookupGeo(async (url) => { if (url !== NETLANE_URL) throw new Error("down"); return netlane; });
check(last.countryCode === "IR", "falls back to netlane when both providers fail");
let allFailed = false;
try { await lookupGeo(async () => { throw new Error("offline"); }); } catch { allFailed = true; }
check(allFailed, "error when every provider fails");

const manifest = JSON.parse(readFileSync(join(root, "manifest.json"), "utf8"));
check(manifest.manifest_version === 3 && manifest.background.service_worker === "background.js", "manifest v3 with service worker");
for (const [, path] of Object.entries(manifest.icons)) check(existsSync(join(root, path)), `icon ${path} exists`);
const en = JSON.parse(readFileSync(join(root, "_locales/en/messages.json"), "utf8"));
const fa = JSON.parse(readFileSync(join(root, "_locales/fa/messages.json"), "utf8"));
const missing = Object.keys(en).filter((k) => !(k in fa));
check(missing.length === 0, `Farsi has every English string${missing.length ? " (missing: " + missing.join(", ") + ")" : ""}`);
const used = new Set();
for (const file of ["popup.html", "options.html", "popup.js", "options.js", "background.js"]) {
  const text = readFileSync(join(root, file), "utf8");
  for (const m of text.matchAll(/data-i18n(?:-title)?="([^"]+)"|\bt\("([^"]+)"|getMessage\("([^"]+)"/g)) used.add(m[1] || m[2] || m[3]);
}
for (const phase of ["latency", "download", "upload"]) used.add(`phase_${phase}`);
const undefinedKeys = [...used].filter((k) => !(k in en));
check(undefinedKeys.length === 0, `every UI string is defined${undefinedKeys.length ? " (undefined: " + undefinedKeys.join(", ") + ")" : ""}`);

if (process.argv.includes("--live")) {
  const live = await lookupGeo();
  check(isCountryCode(live.countryCode), `live lookup (${live.countryCode}, ${live.source})`);
  for (const [url, parse] of [[PRIMARY_URL, parseIpwhois], [FALLBACK_URL, parseIpapi], [NETLANE_URL, parseNetlane]]) {
    try {
      const g = parse(await (await fetch(url, { cache: "no-store" })).json());
      check(isCountryCode(g.countryCode), `live ${new URL(url).host} parses (${g.countryCode})`);
    } catch (e) {
      console.log(`SKIP  live ${new URL(url).host}: ${e.message}`);
    }
  }
  const speed = await import("../lib/speedtest.js");
  const lat = await speed.latency();
  check(lat > 0, `live latency (${Math.round(lat)} ms)`);
  const down = await speed.download();
  check(down > 0, `live download (${down.toFixed(1)} Mbps)`);
  const up = await speed.upload();
  check(up > 0, `live upload (${up.toFixed(1)} Mbps)`);
}

console.log(failures === 0 ? "SELFTEST OK" : `SELFTEST FAILED (${failures})`);
process.exit(failures === 0 ? 0 : 1);
