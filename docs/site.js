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
  document.querySelector("[data-arch-note]").textContent = arm ? "Arm installer" : "64-bit installer";
});

// Show the current release on the button. If GitHub can't be reached, the button just doesn't show a version.
fetch("https://api.github.com/repos/farhang103/loupix/releases/latest")
  .then((response) => (response.ok ? response.json() : null))
  .then((release) => {
    if (!release?.tag_name) return;
    for (const label of document.querySelectorAll("[data-version]")) label.textContent = release.tag_name;
  })
  .catch(() => {});

const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

// A browser can't tell the page how a download is going, so confirm the click and show what happens next.
const downloadButton = document.querySelector(".cta[data-download]");
const downloadLabel = downloadButton.querySelector("[data-cta-label]");
const downloadText = downloadLabel.textContent;
const nextSteps = document.querySelector("[data-next]");
let downloadTimers = [];

function showDownloadStarted(file) {
  downloadTimers.forEach(clearTimeout);
  downloadButton.classList.add("is-busy");
  downloadLabel.textContent = "Starting download…";
  for (const name of document.querySelectorAll("[data-file]")) name.textContent = file;
  nextSteps.hidden = false;
  nextSteps.scrollIntoView({ behavior: reducedMotion ? "auto" : "smooth", block: "nearest" });
  downloadTimers = [
    setTimeout(() => { downloadLabel.textContent = "Downloading…"; }, 2000),
    setTimeout(() => {
      downloadButton.classList.remove("is-busy");
      downloadLabel.textContent = downloadText;
    }, 10000),
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

// Loupix can't be installed from a phone, so offer to carry the link over to a PC.
const copyLink = document.querySelector("[data-copy-link]");
copyLink.addEventListener("click", async () => {
  const note = document.querySelector("[data-copy-note]");
  try {
    await navigator.clipboard.writeText("https://farhang103.github.io/loupix/");
    copyLink.textContent = "Link copied";
    note.textContent = "Now paste it on your PC, or send it to yourself. The download starts from there.";
  } catch {
    note.textContent = "Open farhang103.github.io/loupix on your PC to download it.";
  }
});

// One small animation per tool, showing whether you click or drag.
const CURSOR = '<svg width="16" height="20" viewBox="0 0 16 20" style="position:absolute;left:0;top:0" aria-hidden="true"><path d="M1 1v15l4-3.6 2.6 6 2.7-1.2-2.6-5.8H13z" fill="#FFF" stroke="#000" stroke-width="1.1" stroke-linejoin="round"/></svg>';
const growing = (look) => `<div class="td-grow ${look}" style="left:60px;top:32px;--tw:130px;--th:60px"></div>`;
const drawing = (shape) => `<svg class="td-svg" width="240" height="135" viewBox="0 0 240 135" aria-hidden="true">${shape}</svg>`;
const dragging = (cursor, effect, last = "Let go", held = "") => ({ cursor, ring: "td-rd", effect, held, captions: [["td-cp1", "Press and hold"], ["td-cp2", "Drag"], ["td-cp3", last]] });
const clicking = (cursor, effect, ring = "td-rk") => ({ cursor, ring, effect, captions: [["td-ck", "Click"]] });

const TOOL_LOOKS = {
  select: dragging("td-c-r", `${growing("td-marq")}<div class="td-crop"></div><div class="td-key">Enter</div>`, "Let go, press Enter"),
  arrow: dragging("td-c-a", drawing('<path class="td-draw" pathLength="100" d="M180 100 80.7 50.4" fill="none" stroke="#FF3B30" stroke-width="3.5" stroke-linecap="round"/><polygon class="td-after" points="70,45 77.6,56.7 83.8,44.1" fill="#FF3B30"/>')),
  rect: dragging("td-c-r", growing("td-rect")),
  oval: dragging("td-c-r", growing("td-oval")),
  line: dragging("td-c-a", drawing('<path class="td-draw" pathLength="100" d="M180 100 70 45" fill="none" stroke="#FF3B30" stroke-width="3.5" stroke-linecap="round"/>')),
  text: { cursor: "td-c-t", ring: "td-rk", effect: '<div class="td-label"><span class="td-typed">Look here</span></div>', captions: [["td-ck2", "Click"], ["td-ty", "Type"]] },
  freehand: dragging("td-c-f", drawing('<polyline class="td-drawl" pathLength="100" points="40,100 70,60 100,95 130,55 165,90" fill="none" stroke="#FF3B30" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round"/>')),
  highlighter: dragging("td-c-h", '<div class="td-grow td-hl" style="left:14px;top:44px;min-height:14px;--tw:120px;--th:14px"></div>'),
  spotlight: dragging("td-c-r", growing("td-spot")),
  counter: clicking("td-c-n", '<div class="td-n td-n1" style="left:50px;top:45px">1</div><div class="td-n td-n2" style="left:120px;top:70px">2</div><div class="td-n td-n3" style="left:190px;top:100px">3</div>', "td-r3"),
  pixelate: dragging("td-c-r", growing("td-mosaic")),
  erase: dragging("td-c-r", growing("td-wipe")),
  magnifier: dragging("td-c-m", '<div class="td-src"></div>', "Let go", '<div class="td-bub"><i style="top:13px;width:36px"></i><i style="top:30px;width:26px"></i></div>'),
  ruler: dragging("td-c-u", '<div class="td-rr"></div><div class="td-rlab">130 px</div>'),
  picker: clicking("td-c-i", '<div class="td-chip"><i></i>Copied #E8873A</div>'),
  backdrop: { ...clicking("td-c-b", '<div class="td-sws"><i style="background:#FFF"></i><i style="background:#0A84FF"></i><i style="background:#FFB627"></i><i style="background:#BF5AF2"></i><i style="background:#34C759"></i></div>'), root: "td-bd", page: "td-shrink" },
  dragout: dragging("td-c-d", '<div class="td-win"><b>Chat</b></div><div class="td-grip">⠿</div><div class="td-drop"></div>', "Let go", '<div class="td-ghost"></div>'),
};

const DRAG = "Press and drag";
const TOOL_HELP = [
  ["arrow", "Arrow", DRAG, "Press where the arrow starts, drag to the thing you want to point at, then let go."],
  ["counter", "Step counter", "Click", "Click each spot in order. The numbers 1, 2, 3 appear by themselves."],
  ["text", "Text", "Click, then type", "Click where you want words, type them, then press Enter."],
  ["rect", "Rectangle", DRAG, "Press at one corner and drag to the opposite corner to draw a box around something."],
  ["oval", "Oval", DRAG, "Press and drag to draw a circle around something."],
  ["line", "Line", DRAG, "Press where the line starts, drag to where it ends, then let go."],
  ["freehand", "Freehand", DRAG, "Hold the mouse button down and move to draw, like a pen."],
  ["highlighter", "Highlighter", DRAG, "Hold the mouse button down and sweep across words to mark them in yellow."],
  ["spotlight", "Spotlight", DRAG, "Drag a box around the important part. Everything else gets darker."],
  ["magnifier", "Magnifier", DRAG, "Press on the spot you want bigger, then drag to place the zoomed-in circle."],
  ["pixelate", "Pixelate or blur", DRAG, "Drag over anything private, like a password or a name, to hide it."],
  ["erase", "Erase", DRAG, "Drag over something to wipe it out of the picture."],
  ["ruler", "Ruler", DRAG, "Press at one edge and drag to the other to see the distance."],
  ["picker", "Colour picker", "Click", "Click any colour in your picture to copy its colour code."],
  ["select", "Select and crop", DRAG, "Press on your picture and drag a box around the part you want. Press Enter to keep only that part."],
  ["backdrop", "Backdrop", "Click a style", "Choose a background from the bar. Your picture gets a nice frame for sharing."],
  ["dragout", "Drag into another app", DRAG, "Press this button and drag it into an email, a chat or a folder. Your picture lands there."],
];

function toolAnimation(tool) {
  const look = TOOL_LOOKS[tool];
  const captions = look.captions.map(([timing, words]) => `<span class="td-cap ${timing}">${words}</span>`).join("");
  return `<div class="td ${look.root ?? ""}" aria-hidden="true"><div class="td-page ${look.page ?? ""}"><div class="td-head"></div><div class="td-l" style="top:34px;width:112px"></div><div class="td-l" style="top:48px;width:104px"></div><div class="td-l" style="top:62px;width:118px"></div><div class="td-l" style="top:76px;width:80px"></div><div class="td-btn"></div><div class="td-pic"></div></div>${look.effect}<div class="td-cur ${look.cursor}">${look.held ?? ""}<span class="td-ring ${look.ring}"></span>${CURSOR}</div>${captions}</div>`;
}

const howList = document.querySelector("[data-how]");
howList.innerHTML = TOOL_HELP.map(([tool, name, how, say]) =>
  `<article class="how-tile">${toolAnimation(tool)}<h3>${name}</h3><span class="how-badge">${how}</span><p>${say}</p></article>`).join("");

const howMore = document.querySelector("[data-how-more]");
howMore.addEventListener("click", () => {
  howList.classList.add("is-open");
  howMore.hidden = true;
});

// Each feature recording starts when its card scrolls into view, and rests on a still image otherwise.
const recordings = document.querySelectorAll("img[data-gif]");
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
