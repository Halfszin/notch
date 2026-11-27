// Configurações. Estado em "settings.*" (o Rust guarda); chaves e botões avisam o Rust por ui_set + ui_call.
const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;

const S = {};
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const set = (k, v) => { S[k] = v; draw(); return invoke("ui_set", { key: "settings." + k, value: v }); };
const call = (name, ...args) => invoke("ui_call", { name: "settings." + name, args });
const changed = async (k, v) => { await set(k, v); call("changed"); };

const nav = [["&#xE7F4;", "Notch"], ["&#xE8A9;", "Páginas"], ["&#xE790;", "Aparência"], ["&#xEA8F;", "Notificações"], ["&#xE706;", "Clima"],
  ["&#xE8D4;", "IA"], ["&#xE99A;", "Agentes"], ["&#xE895;", "Atualizações"], ["&#xE7E8;", "App"]];
let section = 0;
$("nav").innerHTML = nav.map(([g, t], i) => `<button class="navitem" data-i="${i}"><span class="g">${g}</span>${t}</button>`).join("");
// Clicar no menu rola até a seção (como no Orbi em C#).
$("nav").addEventListener("click", (e) => {
  const b = e.target.closest("[data-i]");
  if (!b) return;
  section = +b.dataset.i;
  const sc = $("scroll");
  sc.scrollTo({ top: $("s" + section).offsetTop - sc.offsetTop - 8, behavior: "smooth" });
  draw();
});

function draw() {
  document.querySelectorAll(".navitem").forEach((b, i) => b.classList.toggle("on", i === section));
  document.querySelectorAll(".toggle[data-key]").forEach((t) => t.classList.toggle("on", !!S[t.dataset.key]));
  document.querySelectorAll(".seg").forEach((sg) => sg.querySelectorAll("button").forEach((b) => b.classList.toggle("on", b.dataset.v === String(sg.dataset.seg === "edge" ? S.edge ?? 0 : !!S.compact))));
  const pages = S.page_list || [];
  const h = pages.map((p, i) => `<div class="page-row"><span class="num">${i + 1}.</span><span class="nm ${p.visible ? "" : "off"}">${esc(p.name)}</span>`
    + `<button class="mv" data-mv="${i},-1" ${i > 0 ? "" : "disabled"}>&#xE70E;</button><button class="mv" data-mv="${i},1" ${i < pages.length - 1 ? "" : "disabled"}>&#xE70D;</button>`
    + (p.fixed ? `<span class="fixed">sempre</span>` : `<button class="toggle ${p.visible ? "on" : ""}" data-page="${i}"></button>`) + `</div>`).join("");
  if ($("pages").dataset.h !== h) { $("pages").dataset.h = h; $("pages").innerHTML = h; }
  for (const [id, k] of [["city-status", "city_status"], ["api-key-status", "api_key_status"], ["agents-status", "agents_status"], ["update-status", "update_status"]]) $(id).textContent = S[k] || "";
  $("version").textContent = "Orbi " + (S.version || "");
  if (document.activeElement !== $("city")) $("city").value = S.city || "";
  if (document.activeElement !== $("api-key")) $("api-key").value = S.api_key_input || "";
  $("agents-on").style.display = S.agents_connected ? "none" : "";
  $("agents-off").style.display = S.agents_connected ? "" : "none";
  $("restart-update").style.display = S.update_ready ? "" : "none";
}

document.querySelectorAll(".toggle[data-key]").forEach((t) => t.addEventListener("click", () => changed(t.dataset.key, !S[t.dataset.key])));
document.querySelectorAll(".seg").forEach((sg) => sg.addEventListener("click", (e) => {
  const v = e.target.dataset.v;
  if (v == null) return;
  if (sg.dataset.seg === "edge") changed("edge", +v); else changed("compact", v === "true");
}));
$("pages").addEventListener("click", (e) => {
  const mv = e.target.closest("[data-mv]");
  if (mv && !mv.disabled) { const [i, d] = mv.dataset.mv.split(",").map(Number); call("page_move", i, d); }
  const pg = e.target.closest("[data-page]");
  if (pg) call("page_toggle", +pg.dataset.page);
});
const saveCity = () => call("save_city", $("city").value);
$("city").addEventListener("input", (e) => set("city", e.target.value));
$("city").addEventListener("keydown", (e) => { if (e.key === "Enter") saveCity(); });
$("city-save").addEventListener("click", saveCity);
const saveKey = (v) => call("save_api_key", v);
$("api-key").addEventListener("input", (e) => set("api_key_input", e.target.value));
$("api-key").addEventListener("keydown", (e) => { if (e.key === "Enter") saveKey($("api-key").value); });
$("key-save").addEventListener("click", () => saveKey($("api-key").value));
$("key-remove").addEventListener("click", () => saveKey(""));
$("agents-on").addEventListener("click", () => call("agents_connect"));
$("agents-off").addEventListener("click", () => call("agents_disconnect"));
$("check-update").addEventListener("click", () => call("check_update"));
$("restart-update").addEventListener("click", () => call("restart_update"));
$("open-folder").addEventListener("click", () => call("open_folder"));
$("quit").addEventListener("click", () => call("quit"));

(function loop(t) { avatar($("avatar"), 0, 0, 0, t); requestAnimationFrame(loop); })(0);

listen("ui", ({ payload: { k, v } }) => {
  if (!k.startsWith("settings.")) return;
  S[k.slice(9)] = v;
  draw();
}).then(() => invoke("ui_state")).then((all) => {
  for (const k in all) if (k.startsWith("settings.") && !(k.slice(9) in S)) S[k.slice(9)] = all[k];
  draw();
});
