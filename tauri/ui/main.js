// Tela do notch. O Rust guarda o estado ("notch.*") e manda cada mudança no evento "ui";
// os botões chamam os callbacks do Rust (ui_call) e os valores que a tela muda vão por ui_set.
const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;

const S = {};
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
const set = (k, v) => { S[k] = v; render(); return invoke("ui_set", { key: "notch." + k, value: v }); };
const call = (name, ...args) => invoke("ui_call", { name: "notch." + name, args });

// ---------- desenho ----------
let queued = false;
function render() { if (!queued) { queued = true; requestAnimationFrame(() => { queued = false; draw(); }); } }

const memo = new WeakMap();
function html(el, h) { if (memo.get(el) !== h) { memo.set(el, h); el.innerHTML = h; } }
function text(el, t) { t = String(t ?? ""); if (el.textContent !== t) el.textContent = t; }

const GREEN = "#30D158", YELLOW = "#FFD60A", RED = "#FF453A", GRAY = "#8E8E93";

// Anel: trilho + arco no sentido horário a partir do topo, pontas redondas.
function ring(size, stroke, value, tint, bolt = false, label = "") {
  const r = (size - stroke) / 2, c = size / 2, v = clamp(value);
  const dash = v >= 0.999 ? 100 : (v * 100).toFixed(1);
  const arc = v > 0.01 ? `<circle class="arc" cx="${c}" cy="${c}" r="${r}" stroke="${tint}" stroke-width="${stroke}" pathLength="100" stroke-dasharray="${dash} 100"/>` : "";
  return `<span class="ring" style="width:${size}px;height:${size}px"><svg viewBox="0 0 ${size} ${size}"><circle class="track" cx="${c}" cy="${c}" r="${r}" stroke-width="${stroke}"/>${arc}</svg>`
    + (bolt ? `<span class="bolt" style="color:${tint}">&#xE945;</span>` : "") + (label ? `<span class="txt">${esc(label)}</span>` : "") + `</span>`;
}
const bars = (bands) => (bands || []).map((b) => `<i style="height:${(3 + 11 * Math.min(1, b)).toFixed(1)}px"></i>`).join("");

const titles = ["Tocando", "Tarefas", "Foco", "Calendário", "Notas", "Copiados", "Sistema", "Atalhos", "Agentes"];
const subtitles = ["agora", "hoje", "timer", "do mês", "rápidas", "recentes", "agora", "rápidos", "Claude Code"];
const brands = ["WhatsApp", "Discord", "Gmail", "Outlook"];
const claudeSvg = (cls = "") => `<svg class="${cls}" viewBox="0 0 256 257"><use href="#claude"/></svg>`;

let wasScAdding = false, volHover = false;

function draw() {
  const side = S.edge !== 0 && S.edge != null;
  const open = !S.dragging && !!(S.hovered || S.adding || S.notes_focused || S.chat_focused || (S.perm_visible && !S.game_mode));
  const order = S.order || [0];
  const pos = Math.min(S.pos || 0, order.length - 1);
  const page = order[Math.max(0, pos)];
  const chat = !!S.chat_open;
  const withLyrics = S.lyrics_on && S.has_lyrics;
  const showTemp = !S.has_battery && S.temp >= 0;
  const ringColor = !S.has_battery ? GRAY : S.charging || S.battery > 0.5 ? GREEN : S.battery > 0.2 ? YELLOW : RED;
  const tempColor = S.temp < 60 ? GREEN : S.temp < 80 ? YELLOW : RED;
  const b = document.body.classList;
  const flags = {
    e0: !side, e1: S.edge === 1, e2: S.edge === 2, open, toasting: !!S.toasting, "hide-closed": open || S.toasting || S.game_mode,
    playing: !!S.playing, compact: !!S.compact, chat, game: !!S.game_mode, focusing: !!S.focus_running,
    "has-cover": !!S.has_cover, "has-lyrics": !!S.has_lyrics, "lyrics-on": !!S.lyrics_on, "with-lyrics": !!withLyrics,
    adding: !!S.adding && page === 1, "no-tasks": !(S.tasks || []).length, "sc-adding": !!S.sc_adding, perm: !!S.perm_visible, "no-agents": !(S.agent_rows || []).length,
  };
  // Abriu o notch: o personagem dá um pulinho de "oi".
  if (open && !b.contains("open")) setTimeout(() => hero.react("hop"), 250);
  for (const k in flags) b.toggle(k, !!flags[k]);

  // ---- pílula: tamanho, cantos, batida e fundo ----
  const openW = S.compact ? 544 : 620, openH = S.compact ? 232 : 262;
  const closedW = side ? (S.game_mode ? 6 : 34) : S.game_mode ? 70 : 120;
  const closedH = S.game_mode ? (side ? 70 : 6) : !side ? 30 : S.playing ? 116 : 92;
  const w = open ? openW : S.toasting ? 360 : closedW, h = open ? openH : S.toasting ? 36 : closedH;
  const pill = $("pill");
  pill.classList.toggle("grow", open || !!S.toasting);
  pill.style.width = w + "px";
  pill.style.height = h + "px";
  const r = open ? 30 : S.game_mode && !S.toasting ? 3 : side ? 16 : 28;
  pill.style.borderRadius = S.edge === 1 ? `0 ${r}px ${r}px 0` : S.edge === 2 ? `${r}px 0 0 ${r}px` : `0 0 ${r}px ${r}px`;
  const beat = open || S.game_mode ? 1 : 1 + 0.04 * (S.level || 0) + 0.05 * (S.pulse || 0);
  pill.style.setProperty("--beat", beat.toFixed(3));
  pill.style.background = open && chat ? "linear-gradient(180deg, #000 0%, #000 35%, #D97757 135%)"
    : open && page === 0 && S.has_cover && S.gradient_enabled ? `linear-gradient(180deg, #000 0%, #000 30%, ${S.accent} 100%)` : "#000";

  // ---- fechado ----
  const rTop = (S.has_battery ? ring(18, 2.5, S.battery, ringColor, S.charging) : "") + (showTemp ? ring(18, 2.5, S.temp / 100, tempColor) : "");
  const rSmall = (S.has_battery ? ring(16, 2.5, S.battery, ringColor, S.charging) : "") + (showTemp ? ring(16, 2.5, S.temp / 100, tempColor) : "");
  if (!open) {
    html(document.querySelector("#closed-top .rings"), rTop);
    html(document.querySelector("#closed-side .rings"), rSmall);
    html($("closed-bars"), bars(S.bands));
    html($("side-bars"), bars(S.bands));
  }
  text($("closed-text"), S.closed_text || S.clock);
  text($("side-text"), S.side_text);

  // ---- aviso ----
  const brand = brands.includes(S.toast_app);
  html($("toast-icon"),
    (!S.toast_badge ? `<span class="glyph" style="color:${S.toast_color}">${esc(S.toast_glyph)}</span>` : "")
    + (brand ? `<img src="icons/${S.toast_app.toLowerCase()}.png" alt="">` : "")
    + (S.has_toast_icon && !brand ? `<img src="${S.toast_icon}" alt="">` : "")
    + (S.toast_badge && !S.has_toast_icon && !brand ? `<span class="badge" style="background:${S.toast_color}">${esc(S.toast_glyph)}</span>` : ""));
  text($("toast-text"), S.toast_text);

  if (!open) return; // aberto escondido: o resto só importa quando aparece

  // ---- topo ----
  const t = $("title");
  t.className = chat ? "claude" : page === 0 ? "music" : "";
  html(t, chat ? "Claude"
    : page === 0 ? `<span data-lyr="0" class="${S.lyrics_on ? "" : "on"}">Tocando</span><span data-lyr="1" class="${S.lyrics_on ? "on" : ""}">Letra</span>`
    : `${titles[page]}<span class="sub">${subtitles[page]}</span>`);
  html(document.querySelector("#when .rings"), rSmall);
  $("weather").style.display = S.weather_text ? "" : "none";
  text($("weather-glyph"), S.weather_glyph);
  text($("weather-text"), S.weather_text);
  text($("date"), S.date_text);
  text($("clock"), S.clock);

  // ---- páginas ----
  for (let i = 0; i <= 8; i++) $("p" + i).classList.toggle("show", !chat && page === i);
  $("chat").classList.toggle("show", chat);
  if (chat) drawChat();
  else if (page === 0) drawMusic();
  else if (page === 1) drawTasks();
  else if (page === 2) drawFocus();
  else if (page === 3) drawCalendar();
  else if (page === 4) { if (document.activeElement !== $("notes") && $("notes").value !== (S.notes || "")) $("notes").value = S.notes || ""; }
  else if (page === 5) drawClips();
  else if (page === 6) drawSystem(ringColor, tempColor);
  else if (page === 7) drawShortcuts();
  else if (page === 8) drawAgents();

  html($("dots"), order.map((_, i) => `<i data-pos="${i}" class="${i === pos ? "on" : ""}"></i>`).join(""));
  $("arrow-l").classList.toggle("can", pos > 0);
  $("arrow-r").classList.toggle("can", pos < order.length - 1);
}

function drawMusic() {
  const cover = $("cover").querySelector("img");
  if (S.has_cover && cover.getAttribute("src") !== S.cover) cover.src = S.cover;
  text($("song"), S.song);
  text($("artist"), S.artist);
  text($("elapsed"), S.elapsed);
  text($("remaining"), S.remaining);
  $("progress").style.width = clamp(S.progress || 0) * 100 + "%";
  html($("play-btn"), S.playing ? "&#xE769;" : "&#xE768;");
  const v = clamp(S.volume ?? 0.5);
  html($("vol-icon"), v < 0.01 ? "&#xE74F;" : v < 0.34 ? "&#xE993;" : v < 0.67 ? "&#xE994;" : "&#xE995;");
  $("volume").classList.toggle("expanded", volHover || !!S.volume_held);
  document.querySelector(".vfill").style.width = 68 * v + "px";
  document.querySelector(".vknob").style.left = 30 + 68 * v - 6 + "px";
  text($("ly-prev"), S.lyric_prev);
  text($("ly-now"), S.lyric_now);
  text($("ly-next"), S.lyric_next);
  text($("ly-next2"), S.lyric_next2);
}

function drawTasks() {
  text($("tasks-count"), `${S.tasks_done || 0}/${S.tasks_total || 0}`);
  const tasks = S.tasks || [];
  html($("task-list"), tasks.map((k, i) =>
    `<div class="task ${k.done ? "done" : ""}" data-task="${i}"><span class="check">${k.done ? "&#xE73E;" : ""}</span><span class="t">${esc(k.title)}</span>${k.due ? `<span class="due">${esc(k.due)}</span>` : ""}</div>`).join(""));
}

function drawFocus() {
  text($("focus-text"), S.focus_text || "25:00");
  html($("focus-toggle"), S.focus_running ? `<span class="icon">&#xE769;</span>Pausar` : `<span class="icon">&#xE768;</span>Iniciar`);
  html($("presets"), [5, 15, 25, 50].map((m) => `<button class="preset ${(S.focus_minutes ?? 25) === m ? "on" : ""}" data-min="${m}">${m}m</button>`).join(""));
}

function drawCalendar() {
  text($("month-name"), S.month_name);
  html($("cal"), ["D", "S", "T", "Q", "Q", "S", "S"].map((d) => `<span class="wd">${d}</span>`).join("")
    + (S.days || []).map((c) => `<span class="${c.today ? "today" : c.in_month ? "" : "out"}">${c.today ? `<b>${c.day}</b>` : c.day}</span>`).join(""));
}

function drawClips() {
  const clips = S.clips || [];
  text($("clip-hint"), clips.length ? S.clip_hint || "Clique para copiar de novo" : "Copie um texto (Ctrl+C) e ele aparece aqui.");
  $("clip-clear").style.display = clips.length ? "" : "none";
  html($("clip-list"), clips.map((c, i) => `<div class="clip" data-clip="${i}">${esc(c)}</div>`).join(""));
}

function gauge(value, label, name, detail, tint) {
  return `<div class="gauge">${ring(74, 7, value, tint, false, label)}<span class="n">${esc(name)}</span><span class="d">${esc(detail)}</span></div>`;
}
function drawSystem(ringColor, tempColor) {
  html($("gauges"), gauge(S.cpu || 0, S.cpu_text || "—", "CPU", "processador", S.cpu > 0.85 ? RED : GREEN)
    + gauge(S.ram || 0, S.ram_text || "—", "Memória", S.ram_detail, S.ram > 0.9 ? RED : "#0A84FF")
    + (S.has_battery ? gauge(S.battery, Math.round(S.battery * 100) + "%", "Bateria", S.battery_detail, ringColor) : "")
    + (S.temp >= 0 ? gauge(S.temp / 100, Math.round(S.temp) + "°", "Temperatura", "do PC", tempColor) : ""));
  text($("audio-device"), S.audio_device || "Saída de áudio");
}

function drawShortcuts() {
  html($("sc-grid"), (S.shortcuts || []).map((s, i) =>
    `<div class="sc" data-sc="${i}"><span class="box" style="color:${s.tint}">${esc(s.glyph)}</span><span class="nm">${esc(s.name)}</span></div>`).join(""));
}

function drawAgents() {
  text($("perm-what"), S.perm_what);
  text($("perm-project"), (S.perm_project || "") + (S.perm_count > 1 ? `  ·  +${S.perm_count - 1}` : ""));
  text($("perm-code"), S.perm_code);
  const e = $("agents-empty");
  agentsAvatar.mood = S.agents_connected ? "idle" : "sleepy";
  text(e.querySelector("b"), S.agents_connected ? "Nenhuma sessão do Claude Code agora" : "Claude Code não está conectado");
  text(e.querySelector("span"), S.agents_connected ? "Abra o Claude Code: as sessões, o que ele está fazendo e os pedidos de permissão aparecem aqui." : "Conecte em Configurações → Agentes.");
  html($("agent-list"), (S.agent_rows || []).map((a) =>
    `<div class="agent ${a.busy ? "busy" : ""}"><span class="dot"></span><b>${esc(a.project)}</b><span>${esc(a.status)}</span></div>`).join(""));
  html($("plan"), [["5 horas", S.plan_five, S.plan_five_text], ["Semana", S.plan_week, S.plan_week_text]].filter((p) => p[1] >= 0).map(([n, v, t]) =>
    `<div class="plan"><span class="n">${n}</span><span class="bar"><i style="width:${clamp(v) * 100}%;background:${v > 0.85 ? RED : v > 0.6 ? YELLOW : GREEN}"></i></span><span class="t">${esc(t)}</span></div>`).join(""));
}

function drawChat() {
  const msgs = S.chat_messages || [];
  const list = $("chat-list");
  const h = (!msgs.length && !S.chat_busy ? `<div class="greet">${claudeSvg()}<b>${esc(S.chat_greeting)}</b></div>` : "")
    + msgs.map((m) => `<div class="msg ${m.user ? "user" : ""}">${esc(m.text)}</div>`).join("")
    + (S.chat_busy ? `<div class="thinking">Claude está pensando…</div>` : "");
  // Rola sozinho para o fim quando entra pergunta, "pensando…" ou resposta.
  if (memo.get(list) !== h) { html(list, h); list.scrollTop = list.scrollHeight; }
  const input = $("chat-input");
  input.disabled = !!S.chat_busy;
  input.placeholder = msgs.length ? "Responder ao Claude…" : "Como posso ajudar?";
  $("chat-send").classList.toggle("can", input.value !== "" && !S.chat_busy);
}

// Personagem: segue o mouse e dança com a música no player; feliz sem tarefas; dorme sem o Claude Code.
const isOpen = () => document.body.classList.contains("open");
const hero = addAvatar($("avatar"), {}, () => ({ lookX: S.look_x ?? 0, lookY: S.look_y ?? 0, playing: S.playing, level: S.level, pulse: S.pulse }), isOpen);
addAvatar($("tasks-avatar"), { mood: "happy" }, () => ({ lookX: S.look_x ?? 0, lookY: S.look_y ?? 0 }), isOpen);
const agentsAvatar = addAvatar($("agents-avatar"), {}, () => ({}), isOpen);

// ---------- ações ----------
const pill = $("pill");
const on = (id, ev, f) => $(id).addEventListener(ev, f);

on("chat-btn", "click", async () => { const v = !S.chat_open; await set("chat_open", v); if (v) call("chat_opened"); });
on("game-btn", "click", async () => { await set("game_mode", !S.game_mode); call("game_toggled"); });
on("gear-btn", "click", () => call("open_settings"));
on("quit-btn", "click", () => call("quit"));
async function setLyrics(v) { await set("lyrics_on", v); call("lyrics_toggled"); }
on("title", "click", (e) => { const l = e.target.dataset.lyr; if (l != null) setLyrics(l === "1"); });
on("lyr-btn", "click", () => setLyrics(!S.lyrics_on));
on("prev-btn", "click", () => call("previous"));
on("play-btn", "click", () => call("play_pause"));
on("next-btn", "click", () => call("next"));

// Volume: com o mouse em cima abre a barrinha (clicar, arrastar ou rodinha).
const vol = $("volume");
function setVol(px) { const v = clamp((px - 30) / 68); set("volume", v); call("volume_changed", v); }
vol.addEventListener("pointerenter", () => { volHover = true; render(); });
vol.addEventListener("pointerleave", () => { volHover = false; render(); });
vol.addEventListener("pointerdown", (e) => {
  e.stopPropagation();
  if (e.offsetX > 24) { vol.setPointerCapture(e.pointerId); set("volume_held", true); setVol(e.offsetX); }
});
vol.addEventListener("pointermove", (e) => { if (S.volume_held) setVol(e.clientX - vol.getBoundingClientRect().left); });
vol.addEventListener("pointerup", () => { if (S.volume_held) set("volume_held", false); });
vol.addEventListener("wheel", (e) => {
  e.preventDefault(); e.stopPropagation();
  const v = clamp((S.volume ?? 0.5) + (e.deltaY < 0 ? 0.05 : e.deltaY > 0 ? -0.05 : 0));
  set("volume", v); call("volume_changed", v);
}, { passive: false });

// Tarefas
on("task-add", "click", async () => {
  await set("adding", true); call("want_keyboard"); draw(); $("task-input").focus();
});
on("task-clear", "click", () => call("clear_done"));
on("task-input", "keydown", (e) => {
  const inp = e.target;
  if (e.key === "Enter") { if (inp.value.trim()) call("add_task", inp.value); inp.value = ""; set("adding", false); inp.blur(); }
  if (e.key === "Escape") { inp.value = ""; set("adding", false); inp.blur(); }
});
on("task-list", "click", (e) => { const r = e.target.closest("[data-task]"); if (r) call("toggle_task", +r.dataset.task); });

// Foco
on("focus-toggle", "click", () => call("focus_toggle"));
on("focus-reset", "click", () => call("focus_reset"));
on("presets", "click", (e) => { const m = e.target.dataset.min; if (m) call("focus_preset", +m); });

// Calendário
on("month-prev", "click", () => call("month_prev"));
on("month-next", "click", () => call("month_next"));

// Notas: o notch fica aberto enquanto você digita.
on("notes", "focus", () => { set("notes_focused", true); call("want_keyboard"); });
on("notes", "blur", () => set("notes_focused", false));
on("notes", "input", (e) => { S.notes = e.target.value; invoke("ui_set", { key: "notch.notes", value: S.notes }); call("notes_edited", S.notes); });

// Copiados
on("clip-list", "click", (e) => { const r = e.target.closest("[data-clip]"); if (r) call("copy_clip", +r.dataset.clip); });
on("clip-clear", "click", () => call("clear_clips"));

// Atalhos: clique abre; botão direito remove (só os seus).
on("sc-grid", "click", (e) => { const r = e.target.closest("[data-sc]"); if (r) call("open_shortcut", +r.dataset.sc); });
on("sc-grid", "contextmenu", (e) => { e.preventDefault(); const r = e.target.closest("[data-sc]"); if (r) call("remove_shortcut", +r.dataset.sc); });
on("sc-file", "click", () => call("add_shortcut_file"));
on("sc-folder", "click", () => call("add_shortcut_folder"));
on("sc-site", "keydown", (e) => { if (e.key === "Enter") { call("add_shortcut_site", e.target.value); e.target.value = ""; } });
on("sc-cancel", "click", async () => { await set("sc_adding", false); set("adding", false); });

// Agentes: resposta ao pedido de permissão.
on("perm", "click", (e) => { const b = e.target.closest("[data-perm]"); if (b) call("perm_decide", b.dataset.perm); });

// Chat
const chatInput = $("chat-input");
function sendChat() { if (chatInput.value !== "" && !S.chat_busy) { call("chat_send", chatInput.value); chatInput.value = ""; render(); } }
chatInput.addEventListener("focus", () => { set("chat_focused", true); call("want_keyboard"); });
chatInput.addEventListener("blur", () => set("chat_focused", false));
chatInput.addEventListener("input", render);
chatInput.addEventListener("keydown", (e) => { if (e.key === "Enter") sendChat(); });
on("chat-send", "click", sendChat);
on("chat-trash", "click", () => call("chat_clear"));

// Páginas: bolinhas, setas e rodinha do mouse.
const setPos = (p) => set("pos", clamp(p, 0, (S.order || [0]).length - 1));
on("dots", "click", (e) => { const p = e.target.dataset.pos; if (p != null) setPos(+p); });
on("arrow-l", "click", () => setPos((S.pos || 0) - 1));
on("arrow-r", "click", () => setPos((S.pos || 0) + 1));
pill.addEventListener("wheel", (e) => {
  const sc = e.target.closest(".scroll, textarea");
  if (sc && sc.scrollHeight > sc.clientHeight) return; // a lista rola primeiro
  if (e.deltaY > 0) setPos((S.pos || 0) + 1); else if (e.deltaY < 0) setPos((S.pos || 0) - 1);
});

// Segurar e arrastar o fundo do notch: ele corre pela borda e pula para a borda mais perto.
let dragging = false, moveQueued = false;
pill.addEventListener("pointerdown", (e) => {
  if (e.button !== 0 || e.target.closest("button, input, textarea, [data-task], [data-clip], [data-sc], [data-pos], [data-lyr], #volume, .msg, .avatar, .mini-avatar")) return;
  dragging = true; pill.setPointerCapture(e.pointerId); call("drag_start");
});
pill.addEventListener("pointermove", () => {
  if (!dragging || moveQueued) return;
  moveQueued = true; requestAnimationFrame(() => { moveQueued = false; call("drag_move"); });
});
const endDrag = () => { if (dragging) { dragging = false; call("drag_end"); } };
pill.addEventListener("pointerup", endDrag);
pill.addEventListener("pointercancel", endDrag);

// ---------- estado vindo do Rust ----------
listen("ui", ({ payload: { k, v } }) => {
  if (!k.startsWith("notch.")) return;
  const key = k.slice(6);
  S[key] = v;
  if (key === "sc_adding") {
    // A linha de adicionar site aparece: segura o notch aberto e pede o teclado.
    if (v && !wasScAdding) { set("adding", true); call("want_keyboard"); setTimeout(() => $("sc-site").focus(), 0); }
    if (!v && wasScAdding) set("adding", false);
    wasScAdding = !!v;
  }
  render();
}).then(() => invoke("ui_state")).then((all) => {
  for (const k in all) if (k.startsWith("notch.") && !(k.slice(6) in S)) S[k.slice(6)] = all[k];
  render();
});
