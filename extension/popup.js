import { flagPath, formatSpeed } from "./lib/core.js";
import { applyTheme, formatTime, loadSettings, localize, t } from "./i18n.js";

const $ = (id) => document.getElementById(id);
let settings;
let copiedTimer;

function countryName(geo) {
  try {
    return new Intl.DisplayNames([chrome.i18n.getUILanguage()], { type: "region" }).of(geo.countryCode) || geo.country;
  } catch {
    return geo.country;
  }
}

async function render() {
  const { geo, error, speedResult, speedErrors } = await chrome.storage.local.get(["geo", "error", "speedResult", "speedErrors"]);
  const { refreshing, speedTest } = await chrome.storage.session.get(["refreshing", "speedTest"]);

  $("flag").src = geo && flagPath(geo.countryCode) ? flagPath(geo.countryCode) : "icons/icon-128.png";
  $("country").textContent = geo ? countryName(geo) : navigator.onLine ? t("locating") : t("offline");
  $("city").textContent = geo ? [geo.city, geo.fetchedAt && t("updated", formatTime(geo.fetchedAt))].filter(Boolean).join(" · ") : "—";
  $("geo-error").hidden = !error;
  $("geo-error").textContent = error || "";
  if (!copiedTimer) $("ip").textContent = geo?.ip || "—";
  $("isp").textContent = [geo?.isp, geo?.asn].filter(Boolean).join(" · ") || "—";
  $("timezone").textContent = geo?.timezone || "—";
  $("refresh").classList.toggle("spinning", !!refreshing);
  $("refresh").disabled = !!refreshing;

  // The browser's own rough estimate; extensions can't see the real adapter (Wi-Fi/Ethernet).
  const conn = navigator.connection;
  $("connection-row").hidden = !conn?.effectiveType;
  if (conn?.effectiveType) $("connection").textContent = t("browserEstimate", conn.effectiveType.toUpperCase());

  $("latency").textContent = speedResult?.latencyMs != null ? `${Math.round(speedResult.latencyMs)} ms` : "—";
  $("download").textContent = speedResult?.downloadMbps != null ? formatSpeed(speedResult.downloadMbps, settings.unit) : "—";
  $("upload").textContent = speedResult?.uploadMbps != null ? formatSpeed(speedResult.uploadMbps, settings.unit) : "—";
  const failures = (speedErrors || []).map((e) => t(e));
  $("speed-error").hidden = failures.length === 0 || !!speedTest?.phase;
  $("speed-error").textContent = failures.length ? t("failed", failures.join(document.documentElement.dir === "rtl" ? "، " : ", ")) : "";
  const phase = speedTest?.phase;
  $("speed-status").textContent = phase ? t(`phase_${phase}`) : speedResult?.date ? t("lastRun", formatTime(speedResult.date)) : "";
  $("run-test").disabled = !!phase || !navigator.onLine;
}

async function init() {
  localize();
  settings = await loadSettings();
  applyTheme(settings);
  await render();

  $("refresh").addEventListener("click", () => chrome.runtime.sendMessage({ type: "refresh" }));
  $("run-test").addEventListener("click", () => chrome.runtime.sendMessage({ type: "speedTest" }));
  $("open-settings").addEventListener("click", () => chrome.runtime.openOptionsPage());
  $("ip").addEventListener("click", async () => {
    const ip = $("ip").textContent;
    if (!ip || ip === "—" || copiedTimer) return;
    await navigator.clipboard.writeText(ip);
    $("ip").textContent = t("copied") + " ✓";
    copiedTimer = setTimeout(() => {
      copiedTimer = null;
      render();
    }, 1200);
  });

  chrome.storage.onChanged.addListener(async (changes, area) => {
    if (area === "sync" && changes.settings) {
      settings = await loadSettings();
      applyTheme(settings);
    }
    render();
  });

  // Refresh when the popup opens if the data is more than a minute old.
  const { geo } = await chrome.storage.local.get("geo");
  if (!geo || Date.now() - (geo.fetchedAt || 0) > 60_000) chrome.runtime.sendMessage({ type: "refresh" });
}

init();
