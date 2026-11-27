// Avatar do Orbi (olhos que piscam e seguem o mouse, respira, pulsa com o som)
function avatar(el, lx, ly, pulse, t) {
  if (!el.firstChild) el.innerHTML = `<div class="body"><i class="eye"></i><i class="eye"></i><svg viewBox="0 0 20 6" preserveAspectRatio="none"><path d="M 1 1 Q 10 7 19 1" fill="none" stroke="#111316" stroke-width="2.5" stroke-linecap="butt" vector-effect="non-scaling-stroke"/></svg></div>`;
  const W = el.clientWidth, H = el.clientHeight;
  if (!W) return;
  const blink = t % 4200 < 140;
  const s = W * 0.82 * (1 + pulse * 0.1);
  const body = el.firstChild;
  Object.assign(body.style, { width: s + "px", height: s + "px", left: (W - s) / 2 + lx * 3 + "px", top: (H - s) / 2 + Math.sin((t / 1000) * 70 * Math.PI / 180) * 2 - ly * 2 + "px" });
  [-1, 1].forEach((side, i) => {
    const ew = s * 0.12;
    Object.assign(body.children[i].style, {
      width: ew + "px", height: (blink ? 2 : s * 0.2) + "px",
      left: s / 2 + side * s * 0.17 - ew / 2 + lx * s * 0.07 + "px",
      top: s * 0.36 - ly * s * 0.06 + (blink ? s * 0.09 : 0) + "px",
    });
  });
  const mw = s * 0.2;
  Object.assign(body.children[2].style, { width: mw + "px", height: s * 0.07 + "px", left: (s - mw) / 2 + lx * s * 0.05 + "px", top: s * 0.66 - ly * s * 0.04 + "px" });
}
