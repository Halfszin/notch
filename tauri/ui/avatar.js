// Personagem do Orbi, desenhado em canvas: bolinha branca com olhos e sorriso, que respira, pisca,
// segue o mouse, dança com a música e reage ao que acontece no notch.
// O desenho e as expressões são do Orbi. Só a mola (Spring) vem do coucou.

// Mola como a .spring(response:dampingFraction:) do SwiftUI.
// Adaptada do coucou (windows/src/core/anim.ts), licença MIT, (c) 2026 Louis Raillé.
class Spring {
  constructor(value, response = 0.5, damping = 0.72) {
    this.value = value; this.target = value; this.velocity = 0;
    this.configure(response, damping);
  }
  configure(response, damping) { this.omega = (2 * Math.PI) / response; this.zeta = damping; }
  step(dt) {
    const steps = Math.max(1, Math.ceil(dt / (1 / 240))), h = dt / steps;
    for (let i = 0; i < steps; i++) {
      const acc = this.omega * this.omega * (this.target - this.value) - 2 * this.zeta * this.omega * this.velocity;
      this.velocity += acc * h;
      this.value += this.velocity * h;
    }
  }
}

const clamp01 = (v) => Math.max(0, Math.min(1, v));
const INK = "#111316";

class Avatar {
  // mood: "idle" (padrão), "happy" (olhos ^ ^) ou "sleepy" (olhos fechados e zzz).
  constructor(el, { mood = "idle" } = {}) {
    this.el = el;
    this.mood = mood;
    this.canvas = document.createElement("canvas");
    this.canvas.style.cssText = "position:absolute;inset:0;width:100%;height:100%";
    el.appendChild(this.canvas);
    this.ctx = this.canvas.getContext("2d");
    this.lookX = new Spring(0, 0.35, 0.75);
    this.lookY = new Spring(0, 0.35, 0.75);
    this.hop = new Spring(0, 0.42, 0.42);      // pulo (sobe e volta quicando)
    this.squash = new Spring(0, 0.28, 0.35);   // achata/estica (+ achata, - estica)
    this.tilt = new Spring(0, 0.5, 0.6);       // inclina a cabeça no ritmo
    this.open = new Spring(1, 0.09, 0.9);      // abertura dos olhos (piscar)
    this.happy = new Spring(mood === "happy" ? 1 : 0, 0.3, 0.9);
    this.surprise = new Spring(0, 0.25, 0.7);
    this.blush = new Spring(0, 0.3, 0.9);
    this.level = 0;
    this.nextBlink = 1.5 + Math.random() * 3;
    this.blinkUntil = 0;
    this.doubleBlink = false;
    this.wander = { x: 0, y: 0, next: 0 };
    this.surpriseUntil = 0;
    this.lastPulse = 0;
    this.hovered = false;
    el.addEventListener("pointerenter", () => { this.hovered = true; });
    el.addEventListener("pointerleave", () => { this.hovered = false; });
    el.addEventListener("click", () => this.react("giggle"));
  }

  /** Reações: "hop" (pulinho), "surprise" (susto com olhos grandes), "giggle" (cócegas). */
  react(kind) {
    if (kind === "hop") { this.squash.velocity += 2.5; this.hop.velocity -= 3.2; }
    if (kind === "surprise") { this.hop.velocity -= 4.2; this.squash.velocity -= 3; this.surpriseUntil = this.t + 1.1; }
    if (kind === "giggle") { this.squash.velocity += 6; this.tilt.velocity += (Math.random() < 0.5 ? -1 : 1) * 2.4; this.giggleUntil = this.t + 0.9; }
  }

  /**
   * Um quadro. inp: { lookX, lookY (-1 a 1, de onde está o mouse; null = olha em volta sozinho),
   * playing, level, pulse (0 a 1, da música) }.
   */
  frame(t, dt, inp = {}) {
    this.t = t;
    const W = this.el.clientWidth, H = this.el.clientHeight;
    if (!W || !H) return;
    const dpr = window.devicePixelRatio || 1;
    if (this.canvas.width !== Math.round(W * dpr) || this.canvas.height !== Math.round(H * dpr)) {
      this.canvas.width = Math.round(W * dpr); this.canvas.height = Math.round(H * dpr);
    }
    const sleepy = this.mood === "sleepy";
    const playing = !!inp.playing && !sleepy;
    this.level += ((playing ? inp.level || 0 : 0) - this.level) * Math.min(1, dt * 3);

    // Para onde olhar: o mouse; sem mouse, olha em volta de vez em quando.
    if (inp.lookX == null || sleepy) {
      if (t > this.wander.next) {
        const rest = Math.random() < 0.4;
        this.wander = { x: rest ? 0 : Math.random() * 1.6 - 0.8, y: rest ? 0 : Math.random() * 1.0 - 0.5, next: t + 1.6 + Math.random() * 2.6 };
      }
      this.lookX.target = sleepy ? 0 : this.wander.x;
      this.lookY.target = sleepy ? 0.3 : this.wander.y;
    } else {
      this.lookX.target = inp.lookX;
      this.lookY.target = inp.lookY;
    }

    // Dança: a cada batida forte dá um tranco e inclina para o outro lado.
    const pulse = playing ? inp.pulse || 0 : 0;
    if (pulse > 0.6 && this.lastPulse <= 0.6) {
      this.squash.velocity += 1.6 * pulse;
      this.tiltSide = -(this.tiltSide || 1);
      this.tilt.target = this.tiltSide * 0.07;
    }
    if (!playing) this.tilt.target = 0;
    this.lastPulse = pulse;

    // Piscar: a cada 2 a 6 s, às vezes duas vezes seguidas.
    if (t > this.nextBlink) {
      this.blinkUntil = t + 0.13;
      this.nextBlink = this.doubleBlink ? t + 0.22 : t + 2 + Math.random() * 4;
      this.doubleBlink = !this.doubleBlink && Math.random() < 0.18;
    }
    const surprised = t < this.surpriseUntil;
    const giggling = t < (this.giggleUntil || 0);
    this.open.target = sleepy ? 0 : t < this.blinkUntil ? 0 : 1;
    // Olhos ^ ^: humor feliz, música animada, cócegas ou mouse em cima.
    this.happy.target = !surprised && (this.mood === "happy" || giggling || this.hovered || (playing && this.level > 0.5)) ? 1 : 0;
    this.surprise.target = surprised ? 1 : 0;
    this.blush.target = this.hovered || giggling ? 1 : 0;
    this.squash.target = playing ? -0.05 * this.level : 0;

    for (const s of [this.lookX, this.lookY, this.hop, this.squash, this.tilt, this.open, this.happy, this.surprise, this.blush]) s.step(dt);
    this.draw(W, H, dpr, t, sleepy);
  }

  draw(W, H, dpr, t, sleepy) {
    const c = this.ctx;
    c.setTransform(dpr, 0, 0, dpr, 0, 0);
    c.clearRect(0, 0, W, H);
    const base = Math.min(W, H) * (sleepy ? 0.62 : 0.82); // dormindo: menor, sobra espaço para o zzz
    const lx = this.lookX.value, ly = this.lookY.value;
    const breathe = Math.sin(t * (sleepy ? 0.8 : 1.25)) * (sleepy ? 0.035 : 0.02);
    const sq = Math.max(-0.22, Math.min(0.22, this.squash.value + breathe));
    const s = base * (1 + this.level * 0.08);
    const rx = (s / 2) * (1 + sq), ry = (s / 2) * (1 - sq);
    // Pés no mesmo lugar: achatar desce o topo, não levanta a base.
    const cx = W / 2 + lx * base * 0.03;
    const floorY = H / 2 + s / 2;
    const cy = floorY - ry + this.hop.value * base * 0.12 - ly * base * 0.015;

    c.save();
    c.translate(cx, floorY);
    c.rotate(this.tilt.value);
    c.translate(-cx, -floorY);

    // Brilho quente em volta (mais forte com a música).
    c.shadowColor = `rgba(255, 226, 184, ${0.33 + this.level * 0.35})`;
    c.shadowBlur = base * 0.3 * (1 + this.level * 0.5);
    const g = c.createRadialGradient(cx - rx * 0.35, cy - ry * 0.4, s * 0.05, cx, cy, Math.max(rx, ry));
    g.addColorStop(0, "#FFFFFF");
    g.addColorStop(0.7, "#FBFAF8");
    g.addColorStop(1, "#E9E6E1");
    c.fillStyle = g;
    c.beginPath();
    c.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
    c.fill();
    c.shadowBlur = 0;

    // Bochechas rosadas (mouse em cima, cócegas).
    const b = clamp01(this.blush.value);
    if (b > 0.01) {
      c.fillStyle = `rgba(255, 140, 150, ${0.35 * b})`;
      for (const side of [-1, 1]) {
        c.beginPath();
        c.ellipse(cx + side * rx * 0.5 + lx * rx * 0.1, cy + ry * 0.12 - ly * ry * 0.08, rx * 0.13, ry * 0.075, 0, 0, Math.PI * 2);
        c.fill();
      }
    }

    // Olhos: pílulas que piscam; viram ^ ^ felizes; crescem no susto; fecham dormindo.
    const ex = rx * 0.34, ey = cy - ry * 0.22 - ly * ry * 0.1;
    const ew = s * 0.12 * (1 + this.surprise.value * 0.25);
    const eh = s * 0.2 * (1 + this.surprise.value * 0.3);
    const happy = clamp01(this.happy.value);
    c.fillStyle = INK; c.strokeStyle = INK; c.lineCap = "round";
    for (const side of [-1, 1]) {
      const x = cx + side * ex + lx * rx * 0.14;
      if (happy > 0.5) {
        // ^ : arco para cima
        c.lineWidth = Math.max(1.5, s * 0.05);
        c.beginPath();
        c.arc(x, ey + eh * 0.15, ew * 0.75, Math.PI * 1.15, Math.PI * 1.85);
        c.stroke();
      } else if (sleepy) {
        // fechado: risquinho curvo para baixo
        c.lineWidth = Math.max(1.5, s * 0.035);
        c.beginPath();
        c.arc(x, ey, ew * 0.7, Math.PI * 0.15, Math.PI * 0.85);
        c.stroke();
      } else {
        const h = Math.max(s * 0.025, eh * clamp01(this.open.value) * (1 - happy * 0.6));
        const w = ew;
        c.beginPath();
        c.roundRect(x - w / 2, ey - h / 2, w, h, w / 2);
        c.fill();
        // brilho no olho
        if (h > eh * 0.6) {
          c.fillStyle = "rgba(255,255,255,0.85)";
          c.beginPath();
          c.arc(x + w * 0.18, ey - h * 0.22, w * 0.16, 0, Math.PI * 2);
          c.fill();
          c.fillStyle = INK;
        }
      }
    }

    // Boca: sorriso (maior quando feliz), "o" no susto.
    const my = cy + ry * 0.3 - ly * ry * 0.06, mx = cx + lx * rx * 0.1;
    c.lineWidth = Math.max(1.5, s * 0.03);
    c.beginPath();
    if (this.surprise.value > 0.4) {
      c.ellipse(mx, my + ry * 0.04, s * 0.04, s * 0.05 * this.surprise.value, 0, 0, Math.PI * 2);
      c.fill();
    } else {
      const mw = s * (0.1 + happy * 0.05), depth = s * (0.035 + happy * 0.03);
      c.moveTo(mx - mw, my);
      c.quadraticCurveTo(mx, my + depth * 2, mx + mw, my);
      c.stroke();
    }
    c.restore();

    // Dormindo: zzz subindo.
    if (sleepy) {
      c.fillStyle = "rgba(255,255,255,0.7)";
      for (let i = 0; i < 3; i++) {
        const p = (t * 0.35 + i / 3) % 1;
        c.globalAlpha = Math.sin(p * Math.PI);
        c.font = `600 ${Math.round(base * (0.14 + i * 0.03))}px "Segoe UI", sans-serif`;
        c.fillText("z", cx + rx * 0.7 + p * base * 0.15, cy - ry * 0.6 - p * base * 0.35);
      }
      c.globalAlpha = 1;
    }
  }
}

// Um laço só para todos os personagens da tela; cada um diz se está visível e de onde vem o humor.
const avatars = [];
function addAvatar(el, opts, input = () => ({}), visible = () => true) {
  const a = new Avatar(el, opts);
  avatars.push({ a, input, visible });
  return a;
}
let lastT = 0;
requestAnimationFrame(function loop(ms) {
  const t = ms / 1000, dt = Math.min(0.05, t - lastT || 0.016);
  lastT = t;
  for (const { a, input, visible } of avatars) if (visible() && a.el.offsetParent) a.frame(t, dt, input());
  requestAnimationFrame(loop);
});
