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
