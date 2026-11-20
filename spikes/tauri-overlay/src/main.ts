import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

type ProcessStats = { cpu_percent: number; memory_mb: number; process_count: number };

const $ = (id: string) => document.getElementById(id)!;
const isTauri = "__TAURI_INTERNALS__" in window;

// ---- Fake game clock + objective timers (mirrors Mango's rune intervals) ----
const timers = [
  { name: "Bounty", every: 180, from: 0 },
  { name: "Power", every: 120, from: 360 },
  { name: "Wisdom", every: 420, from: 420 },
  { name: "Lotus", every: 180, from: 180 },
];

const timerRows = timers.map((t) => {
  const row = document.createElement("div");
  row.className = "row timer";
  row.innerHTML = `<span class="label">${t.name}</span><span class="value"></span>`;
  $("timers").append(row);
  return { t, row, value: row.querySelector(".value") as HTMLElement };
});

const fmt = (s: number) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, "0")}`;
const started = performance.now();

function tickGame() {
  const clock = (performance.now() - started) / 1000;
  $("clock").textContent = fmt(clock);
  $("gpm").textContent = String(Math.round(380 + 60 * Math.sin(clock / 30)));
  $("xpm").textContent = String(Math.round(450 + 80 * Math.cos(clock / 40)));
  for (const { t, row, value } of timerRows) {
    const next = clock < t.from ? t.from : t.from + Math.ceil((clock - t.from) / t.every) * t.every;
    const left = next - clock;
    value.textContent = fmt(left);
    row.classList.toggle("soon", left <= 15);
  }
}
setInterval(tickGame, 250);
tickGame();

// ---- Render FPS probe: only in interactive mode, because a rAF loop forces a repaint
// every frame (165/s on a 165 Hz screen) and would itself cost CPU while Dota runs. ----
let frames = 0;
let probing = false;
const countFrame = () => {
  frames++;
  if (probing) requestAnimationFrame(countFrame);
};
function setFpsProbe(on: boolean) {
  if (on && !probing) requestAnimationFrame(countFrame);
  probing = on;
  frames = 0;
  if (!on) $("fps").textContent = "off (locked)";
}
setInterval(() => {
  if (probing) $("fps").textContent = String(frames);
  frames = 0;
  $("dpi").textContent = `${window.devicePixelRatio.toFixed(2)}×`;
}, 1000);

// ---- Process CPU/RAM from Rust (host + WebView2 children) ----
async function pollStats() {
  if (!isTauri) return;
  const s = await invoke<ProcessStats>("process_stats");
  $("cpu").textContent = `${s.cpu_percent.toFixed(1)}%`;
  $("mem").textContent = `${s.memory_mb.toFixed(0)} MB`;
  $("procs").textContent = String(s.process_count);
}
setInterval(pollStats, 2000);

// ---- Interactive mode + dragging, positions persisted per widget ----
const widgets = Array.from(document.querySelectorAll<HTMLElement>(".widget"));
const storeKey = (el: HTMLElement) => `mango.spike.pos.${el.id}`;

for (const el of widgets) {
  let saved: string | null = null;
  try {
    saved = localStorage.getItem(storeKey(el));
  } catch {}
  const [x, y] = (saved ?? el.dataset.default ?? "0,0").split(",").map(Number);
  el.style.left = `${x}px`;
  el.style.top = `${y}px`;

  el.addEventListener("pointerdown", (e) => {
    if (!document.body.classList.contains("interactive")) return;
    const dx = e.clientX - el.offsetLeft;
    const dy = e.clientY - el.offsetTop;
    el.setPointerCapture(e.pointerId);
    el.classList.add("dragging");
    const move = (m: PointerEvent) => {
      el.style.left = `${Math.max(0, m.clientX - dx)}px`;
      el.style.top = `${Math.max(0, m.clientY - dy)}px`;
    };
    const up = () => {
      el.classList.remove("dragging");
      el.removeEventListener("pointermove", move);
      try {
        localStorage.setItem(storeKey(el), `${el.offsetLeft},${el.offsetTop}`);
      } catch {}
    };
    el.addEventListener("pointermove", move);
    el.addEventListener("pointerup", up, { once: true });
  });
}

function setInteractive(on: boolean) {
  document.body.classList.toggle("interactive", on);
  $("mode-banner").hidden = !on;
  setFpsProbe(on);
}

setInteractive(false);
if (isTauri) {
  listen<{ interactive: boolean }>("mode-changed", (e) => setInteractive(e.payload.interactive));
} else {
  // In a plain browser (npm run vite) there is no hotkey; start in drag mode for layout work.
  setInteractive(true);
}
