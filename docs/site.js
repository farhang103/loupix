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
