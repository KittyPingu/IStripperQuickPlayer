const repository = "KittyPingu/IStripperQuickPlayer";
const releasePage = `https://github.com/${repository}/releases/latest`;
const releaseApi = `https://api.github.com/repos/${repository}/releases/latest`;

async function resolveLatestInstaller() {
  try {
    const response = await fetch(releaseApi, {
      headers: { Accept: "application/vnd.github+json" }
    });
    if (!response.ok) return;

    const release = await response.json();
    const installer = release.assets?.find(asset =>
      /setup.*\.exe$/i.test(asset.name)
    ) ?? release.assets?.find(asset => /\.exe$/i.test(asset.name));
    if (!installer?.browser_download_url) return;

    document.querySelectorAll("[data-download]").forEach(link => {
      link.href = installer.browser_download_url;
      link.title = `Download ${installer.name}`;
    });
    document.querySelectorAll("[data-release-notes]").forEach(link => {
      link.href = release.html_url || releasePage;
    });

    const status = document.querySelector("[data-release-status]");
    if (status)
      status.textContent = `Windows x64 · ${release.tag_name}`;
  } catch {
    // The release-page fallback remains usable if GitHub's API is unavailable.
  }
}

resolveLatestInstaller();

const carouselTabs = [...document.querySelectorAll('[role="tab"]')];
const carouselPanels = [...document.querySelectorAll('[role="tabpanel"]')];
const carouselStatus = document.querySelector("[data-carousel-status]");
console.assert(carouselTabs.length === carouselPanels.length,
  "Carousel tabs and panels must match");

function selectCarouselPanel(id, updateHash = false) {
  const index = carouselPanels.findIndex(panel => panel.dataset.carouselId === id);
  if (index < 0) return;

  carouselPanels.forEach((panel, panelIndex) => {
    const selected = panelIndex === index;
    panel.hidden = !selected;
    carouselTabs[panelIndex].ariaSelected = selected;
    carouselTabs[panelIndex].tabIndex = selected ? 0 : -1;
  });
  carouselStatus.textContent = `${index + 1} / ${carouselPanels.length}`;
  if (updateHash) history.replaceState(null, "", `#${id}`);
}

function moveCarousel(offset) {
  const current = carouselTabs.findIndex(tab => tab.ariaSelected === "true");
  const next = (current + offset + carouselPanels.length) % carouselPanels.length;
  selectCarouselPanel(carouselPanels[next].dataset.carouselId, true);
  carouselTabs[next].focus();
}

carouselTabs.forEach(tab => {
  tab.addEventListener("click", () =>
    selectCarouselPanel(tab.getAttribute("aria-controls").replace("-panel", ""), true));
  tab.addEventListener("keydown", event => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    moveCarousel(event.key === "ArrowLeft" ? -1 : 1);
  });
});

document.querySelector("[data-carousel-previous]").addEventListener("click", () => moveCarousel(-1));
document.querySelector("[data-carousel-next]").addEventListener("click", () => moveCarousel(1));

function selectCarouselFromHash(scroll = false) {
  const id = location.hash.slice(1);
  if (!carouselPanels.some(panel => panel.dataset.carouselId === id)) return;
  selectCarouselPanel(id);
  if (scroll)
    document.querySelector(".showcase-section").scrollIntoView();
}

document.querySelectorAll('.site-nav a[href^="#"]').forEach(link => {
  link.addEventListener("click", event => {
    event.preventDefault();
    history.pushState(null, "", link.hash);
    selectCarouselFromHash(true);
  });
});

addEventListener("popstate", () => selectCarouselFromHash(true));
addEventListener("load", () => selectCarouselFromHash(true), { once: true });
