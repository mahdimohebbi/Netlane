import { DEFAULT_SETTINGS, flagPath, lookupGeo } from "./lib/core.js";
import * as speed from "./lib/speedtest.js";

const ALARM = "refresh";
const REFRESH_MINUTES = 5;

async function getSettings() {
  const { settings } = await chrome.storage.sync.get("settings");
  return { ...DEFAULT_SETTINGS, ...settings };
}

/** Draws the flag letterboxed into a square with rounded corners; toolbar icons must be square. */
async function flagImageData(code, size) {
  const blob = await (await fetch(chrome.runtime.getURL(flagPath(code)))).blob();
  const bitmap = await createImageBitmap(blob);
  const canvas = new OffscreenCanvas(size, size);
  const g = canvas.getContext("2d");
  const h = Math.round(size * 0.75);
  const y = Math.round((size - h) / 2);
  const r = Math.max(1.5, size * 0.12);
  g.beginPath();
  g.roundRect(0, y, size, h, r);
  g.clip();
  g.drawImage(bitmap, 0, y, size, h);
  return g.getImageData(0, 0, size, size);
}

async function updateAction(geo, error) {
  const settings = await getSettings();
  if (geo && flagPath(geo.countryCode)) {
    try {
      await chrome.action.setIcon({ imageData: { 16: await flagImageData(geo.countryCode, 16), 32: await flagImageData(geo.countryCode, 32) } });
    } catch {
      await chrome.action.setIcon({ path: { 16: "icons/icon-16.png", 32: "icons/icon-32.png" } });
    }
    await chrome.action.setTitle({ title: `Netlane — ${geo.country} · ${geo.ip}${error ? " (offline)" : ""}` });
  } else {
    await chrome.action.setIcon({ path: { 16: "icons/icon-16.png", 32: "icons/icon-32.png" } });
    await chrome.action.setTitle({ title: error ? `Netlane — ${chrome.i18n.getMessage("offline")}` : "Netlane" });
  }
  await chrome.action.setBadgeBackgroundColor({ color: error ? "#C46E00" : "#4F46E5" });
  await chrome.action.setBadgeText({ text: error && !geo ? "!" : settings.showBadge && geo ? geo.countryCode : "" });
}

let refreshing = null;

async function refresh(reason) {
  if (refreshing) return refreshing; // coalesce overlapping triggers
  refreshing = (async () => {
    const { geo: previous } = await chrome.storage.local.get("geo");
    await chrome.storage.session.set({ refreshing: true });
    try {
      const geo = await lookupGeo();
      await chrome.storage.local.set({ geo, error: null });
      await updateAction(geo, null);
      if (previous && previous.ip !== geo.ip && reason !== "install") await notifyChange(previous, geo);
    } catch (e) {
      await chrome.storage.local.set({ error: navigator.onLine ? e.message : chrome.i18n.getMessage("noNetwork") });
      await updateAction(previous, e);
    } finally {
      await chrome.storage.session.set({ refreshing: false });
      refreshing = null;
    }
  })();
  return refreshing;
}

async function notifyChange(previous, geo) {
  const settings = await getSettings();
  if (!settings.notifyOnChange) return;
  const sameCountry = previous.countryCode === geo.countryCode;
  chrome.notifications.create(`ip-${Date.now()}`, {
    type: "basic",
    iconUrl: flagPath(geo.countryCode) || "icons/icon-128.png",
    title: sameCountry ? chrome.i18n.getMessage("ipChanged") : chrome.i18n.getMessage("nowIn", [geo.country]),
    message: `${previous.ip} → ${geo.ip}` + (sameCountry ? "" : `\n${chrome.i18n.getMessage("wasIn", [previous.country])}`),
    priority: 0,
  });
}

async function runSpeedTest() {
  const { speedTest } = await chrome.storage.session.get("speedTest");
  if (speedTest?.phase) return;
  const result = { latencyMs: null, downloadMbps: null, uploadMbps: null, date: null };
  const errors = [];
  const phase = (name) => chrome.storage.session.set({ speedTest: { phase: name } });

  await phase("latency");
  try { result.latencyMs = await speed.latency(); } catch { errors.push("latency"); }
  await phase("download");
  try { result.downloadMbps = await speed.download(); } catch { errors.push("download"); }
  await phase("upload");
  try { result.uploadMbps = await speed.upload(); } catch { errors.push("upload"); }

  result.date = Date.now();
  await chrome.storage.session.set({ speedTest: { phase: null } });
  await chrome.storage.local.set({
    speedResult: errors.length < 3 ? result : (await chrome.storage.local.get("speedResult")).speedResult ?? null,
    speedErrors: errors,
  });
}

chrome.runtime.onInstalled.addListener(({ reason }) => {
  chrome.alarms.create(ALARM, { periodInMinutes: REFRESH_MINUTES });
  refresh(reason === "install" ? "install" : "update");
});

chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create(ALARM, { periodInMinutes: REFRESH_MINUTES });
  refresh("startup");
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === ALARM) refresh("alarm");
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "refresh") refresh("manual").then(() => sendResponse({ ok: true }));
  else if (message?.type === "speedTest") runSpeedTest().then(() => sendResponse({ ok: true }));
  else return false;
  return true; // keep the channel open for the async response
});

chrome.storage.onChanged.addListener(async (changes, area) => {
  if (area === "sync" && changes.settings) {
    const { geo, error } = await chrome.storage.local.get(["geo", "error"]);
    updateAction(geo, error);
  }
});

chrome.notifications.onClicked.addListener((id) => {
  chrome.notifications.clear(id);
  chrome.action.openPopup?.().catch(() => {});
});
