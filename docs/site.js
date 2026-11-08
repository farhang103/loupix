// Offer the installer that matches this PC. The page's links already point at x64, which also runs on Windows 11 on Arm.
const INSTALLERS = "https://github.com/farhang103/loupix/releases/latest/download/";

async function detectPC() {
  const hints = navigator.userAgentData;
  if (hints?.getHighEntropyValues) {
    try {
      const { platform, architecture } = await hints.getHighEntropyValues(["architecture"]);
      return { windows: platform === "Windows", arm: architecture === "arm" };
    } catch { /* fall back to the user agent string */ }
  }
  const agent = navigator.userAgent;
  return { windows: /Windows NT/i.test(agent), arm: /\b(ARM64|aarch64)\b/i.test(agent) };
}

detectPC().then(({ windows, arm }) => {
  document.documentElement.classList.add(windows ? "is-windows" : "not-windows");
  if (!windows) return;
  const file = arm ? "Loupix-Setup-arm64.exe" : "Loupix-Setup-x64.exe";
  for (const link of document.querySelectorAll("a[data-download]")) link.href = INSTALLERS + file;
  document.querySelector("[data-arch-note]").textContent = arm ? "Installer for Windows on Arm" : "Installer for 64-bit Windows";
});

// Show the current release on the button. The version written in the page is the fallback if GitHub can't be reached.
fetch("https://api.github.com/repos/farhang103/loupix/releases/latest")
  .then((response) => (response.ok ? response.json() : null))
  .then((release) => {
    if (!release?.tag_name) return;
    for (const label of document.querySelectorAll("[data-version]")) label.textContent = release.tag_name;
  })
  .catch(() => {});

// A browser can't tell the page how a download is going, so confirm the click and say what to do next.
const downloadButton = document.querySelector(".cta[data-download]");
const downloadLabel = downloadButton.querySelector("[data-cta-label]");
const downloadText = downloadLabel.textContent;
const downloadToast = document.querySelector("[data-download-toast]");
let downloadTimers = [];

function showDownloadStarted(file) {
  downloadTimers.forEach(clearTimeout);
  downloadButton.classList.add("is-busy");
  downloadLabel.textContent = "Starting download…";
  downloadToast.textContent = "Starting your download…";
  downloadToast.hidden = false;
  downloadTimers = [
    setTimeout(() => {
      downloadLabel.textContent = "Downloading…";
      downloadToast.textContent = `Downloading ${file}. When it finishes, open it and click Next.`;
    }, 2000),
    setTimeout(() => {
      downloadButton.classList.remove("is-busy");
      downloadLabel.textContent = downloadText;
    }, 10000),
    setTimeout(() => { downloadToast.hidden = true; }, 30000),
  ];
}

document.addEventListener("click", (event) => {
  const link = event.target.closest('a[href$=".exe"]');
  if (!link) return;
  if (link === downloadButton && downloadButton.classList.contains("is-busy")) {
    event.preventDefault(); // a second click shouldn't start a second download
    return;
  }
  showDownloadStarted(link.getAttribute("href").split("/").pop());
});

// Each feature recording starts when its card scrolls into view, and rests on a still image otherwise.
const recordings = document.querySelectorAll("img[data-gif]");
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

if (!reducedMotion && "IntersectionObserver" in window) {
  const observer = new IntersectionObserver((entries) => {
    for (const { target, isIntersecting } of entries) {
      const next = isIntersecting ? target.dataset.gif : target.dataset.still;
      if (target.getAttribute("src") !== next) target.src = next;
    }
  }, { threshold: 0.4 });

  for (const image of recordings) {
    image.dataset.still = image.getAttribute("src");
    observer.observe(image);
  }
}
