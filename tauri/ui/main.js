// Tela do notch: o Rust avisa quando o mouse entra/sai ("notch") e manda a bateria ("battery").
const { listen } = window.__TAURI__.event;

listen("notch", (e) => document.body.classList.toggle("open", e.payload === true));

listen("battery", ({ payload: b }) => {
  document.body.classList.toggle("no-battery", !b.has);
  // Verde (carregando ou acima de 50%), amarelo (21–50%), vermelho (20% ou menos).
  const color = b.charging || b.pct > 50 ? "var(--green)" : b.pct > 20 ? "var(--yellow)" : "var(--red)";
  document.documentElement.style.setProperty("--ring", color);
  document.documentElement.style.setProperty("--pct", Math.max(0.01, b.pct));
});

const weekdays = ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];
const months = ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];
function tick() {
  const d = new Date();
  const hm = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  document.getElementById("clock-closed").textContent = hm;
  document.getElementById("clock-open").textContent = hm;
  document.getElementById("date").textContent = `${weekdays[d.getDay()]} ${d.getDate()} ${months[d.getMonth()]} ${d.getFullYear()}`;
}
tick();
setInterval(tick, 1000);
