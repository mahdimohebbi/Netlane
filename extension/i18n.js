import { DEFAULT_SETTINGS } from "./lib/core.js";

export const t = (key, ...subs) => chrome.i18n.getMessage(key, subs) || key;

/** Fills [data-i18n] text and [data-i18n-title] tooltips, and sets the page direction for Farsi. */
export function localize(root = document) {
  const lang = chrome.i18n.getUILanguage();
  document.documentElement.lang = lang;
  document.documentElement.dir = lang.startsWith("fa") ? "rtl" : "ltr";
  root.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));
  root.querySelectorAll("[data-i18n-title]").forEach((el) => (el.title = t(el.dataset.i18nTitle)));
}

export async function loadSettings() {
  const { settings } = await chrome.storage.sync.get("settings");
  return { ...DEFAULT_SETTINGS, ...settings };
}

export function applyTheme(settings) {
  if (settings.theme === "system") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = settings.theme;
}

export function formatTime(timestamp) {
  return new Date(timestamp).toLocaleTimeString(chrome.i18n.getUILanguage(), { hour: "2-digit", minute: "2-digit" });
}
