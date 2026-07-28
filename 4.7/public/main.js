// Version picker for the CalloraVoipSdk docs (DocFX modern template).
//
// Renders a dropdown in the navbar that lets readers switch between documentation
// versions. Convention: the latest version lives at the site root; older minor lines
// live under /<minor>/ (e.g. /4.5/). The authoritative manifest is /versions.json at
// the site ROOT (written by the release workflow / shipped in source), so every version
// folder reads the same, up-to-date list even for older builds.
//
// The DocFX "modern" template auto-imports /public/main.js as a module and calls the
// default export's start() hook; we also self-invoke on DOMContentLoaded as a fallback.

const VERSION_DIR_RE = /\/(\d+\.\d+)\/$/;

function computePaths() {
  // main.js is served at "{versionRoot}public/main.js".
  const url = new URL(import.meta.url);
  const versionRoot = url.pathname.replace(/public\/main\.js.*$/, "");
  const match = versionRoot.match(VERSION_DIR_RE);
  const currentVersion = match ? match[1] : null; // null = latest (root)
  const base = match ? versionRoot.slice(0, match.index + 1) : versionRoot; // strip trailing /X.Y/
  return { base, currentVersion };
}

async function loadManifest(base) {
  const res = await fetch(base + "versions.json", { cache: "no-cache" });
  if (!res.ok) throw new Error("versions.json HTTP " + res.status);
  return res.json();
}

function buildSelect(manifest, base, currentVersion) {
  const select = document.createElement("select");
  select.className = "voip-version-select form-select form-select-sm";
  select.setAttribute("aria-label", "Documentation version");

  for (const v of manifest.versions) {
    const path = v.path || ""; // "" = latest at root
    const option = document.createElement("option");
    option.value = base + (path ? path + "/" : "");
    option.textContent = v.label;
    const isCurrent = path ? path === currentVersion : !currentVersion;
    if (isCurrent) option.selected = true;
    select.appendChild(option);
  }

  select.addEventListener("change", () => {
    window.location.href = select.value;
  });
  return select;
}

function mount(select) {
  const navbar = document.querySelector("header .navbar");
  if (!navbar) return false;
  if (navbar.querySelector(".voip-version-picker")) return true; // already mounted

  const wrap = document.createElement("div");
  wrap.className = "voip-version-picker";
  wrap.appendChild(select);

  const brand = navbar.querySelector(".navbar-brand");
  if (brand && brand.parentNode) {
    brand.parentNode.insertBefore(wrap, brand.nextSibling);
  } else {
    navbar.appendChild(wrap);
  }
  return true;
}

let injected = false;

async function injectVersionPicker() {
  if (injected) return;
  try {
    const { base, currentVersion } = computePaths();
    const manifest = await loadManifest(base);
    if (!manifest || !Array.isArray(manifest.versions) || manifest.versions.length < 2) {
      return; // nothing to switch between
    }
    injected = true;
    const select = buildSelect(manifest, base, currentVersion);
    // The navbar may not be in the DOM yet; retry briefly.
    let tries = 0;
    const tryMount = () => {
      if (mount(select) || tries++ > 40) return;
      setTimeout(tryMount, 100);
    };
    tryMount();
  } catch (err) {
    // Non-fatal: the docs render fine without the picker.
    console.warn("[voip-docs] version picker unavailable:", err);
  }
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", injectVersionPicker);
} else {
  injectVersionPicker();
}

export default {
  start: injectVersionPicker,
};
