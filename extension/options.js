import { applyTheme, loadSettings, localize, t } from "./i18n.js";

let savedTimer;

async function init() {
  localize();
  const settings = await loadSettings();
  applyTheme(settings);

  document.querySelectorAll('input[type="radio"]').forEach((input) => {
    input.checked = settings[input.name] === input.value;
  });
  document.querySelectorAll('input[type="checkbox"]').forEach((input) => {
    input.checked = !!settings[input.name];
  });

  document.body.addEventListener("change", async (event) => {
    const input = event.target;
    if (!input.name) return;
    settings[input.name] = input.type === "checkbox" ? input.checked : input.value;
    await chrome.storage.sync.set({ settings });
    applyTheme(settings);
    const saved = document.getElementById("saved");
    saved.textContent = t("saved") + " ✓";
    clearTimeout(savedTimer);
    savedTimer = setTimeout(() => (saved.textContent = ""), 1500);
  });
}

init();
