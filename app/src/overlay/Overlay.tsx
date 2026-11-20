import { type ReactNode, useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { api, isTauri } from "../lib/api";
import { useTauriEvent } from "../lib/hooks";
import type { OverlayView, TimerView } from "../lib/types";
import { type WidgetId, PLACEMENTS, clamp, defaultPosition } from "./layout";

const RUNE_ICONS: Record<TimerView["id"], string> = {
  bounty: "/hud/bounty-rune.png",
  power: "/hud/power-rune.png",
  wisdom: "/hud/wisdom-rune.png",
  lotus: "/hud/lotus-pool.png",
};

/** Preview shown when the page runs outside Mango (`npm run vite`). */
const PREVIEW: OverlayView = {
  gpm: 612,
  xpm: 704,
  interactive: true,
  opacity: 0.92,
  showPlayerPanel: true,
  showBuildPanel: true,
  layout: { player: null, bounty: null, power: null, wisdom: null, lotus: null, build: null },
  timers: [
    { id: "bounty", name: "Bounty Rune", countdown: "00:12", secondsUntil: 12, visible: true },
    { id: "power", name: "Power Rune", countdown: "00:12", secondsUntil: 12, visible: true },
    { id: "wisdom", name: "Wisdom Rune", countdown: "03:12", secondsUntil: 192, visible: true },
    { id: "lotus", name: "Lotus Pool", countdown: "00:42", secondsUntil: 42, visible: true },
  ],
  buildItems: ["Power Treads", "Battle Fury", "Manta Style"],
  alert: null,
  toggleInteractiveHint: "Alt+`",
};

/** Dota's own Radiance font from the local install, so text matches the HUD. */
async function loadHudFonts() {
  if (!isTauri) return;
  try {
    const [regular, semibold] = await Promise.all([api.hudFont("regular"), api.hudFont("semibold")]);
    for (const [family, data] of [["Radiance", regular], ["Radiance Semibold", semibold]] as const) {
      const face = new FontFace(family, data);
      await face.load();
      document.fonts.add(face);
    }
  } catch {
    // Segoe UI fallback.
  }
}

function beep() {
  try {
    const audio = new AudioContext();
    const tone = audio.createOscillator();
    const gain = audio.createGain();
    tone.frequency.value = 880;
    gain.gain.setValueAtTime(0.0001, audio.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.15, audio.currentTime + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, audio.currentTime + 0.35);
    tone.connect(gain).connect(audio.destination);
    tone.start();
    tone.stop(audio.currentTime + 0.4);
    tone.onended = () => audio.close();
  } catch {
    // No audio device.
  }
}

export function Overlay() {
  const [view, setView] = useState<OverlayView | null>(isTauri ? null : PREVIEW);
  const [dragOverrides, setDragOverrides] = useState<Partial<Record<WidgetId, [number, number]>>>({});
  const root = useRef<HTMLDivElement>(null);
  const [screen, setScreen] = useState({ width: window.innerWidth, height: window.innerHeight });

  useEffect(() => {
    loadHudFonts();
    api.overlayState().then(setView).catch(() => {});
    const onResize = () => setScreen({ width: window.innerWidth, height: window.innerHeight });
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  useTauriEvent<OverlayView>("overlay-state", (next) => {
    setView(next);
    if (next.alert) beep();
  });

  // Saved positions win once a drag is stored. State arrives every second, so compare by value.
  const savedLayout = JSON.stringify(view?.layout ?? null);
  useEffect(() => setDragOverrides({}), [savedLayout]);

  useRegions(root, view);

  if (!view) return null;
  const interactive = view.interactive;
  const position = (id: WidgetId): [number, number] => dragOverrides[id] ?? view.layout[id] ?? defaultPosition(id, screen.width);
  const widget = (id: WidgetId, visible: boolean, children: ReactNode) =>
    visible && (
      <Widget
        key={id}
        id={id}
        interactive={interactive}
        position={position(id)}
        screen={screen}
        onMove={(p) => setDragOverrides((d) => ({ ...d, [id]: p }))}
        onDrop={(p) => api.overlaySavePosition(id, p[0], p[1]).catch(() => {})}
      >
        {children}
      </Widget>
    );

  return (
    <div ref={root} className="relative h-full w-full" style={{ opacity: view.opacity }}>
      {widget(
        "player",
        view.showPlayerPanel,
        <div className="hud-row" data-edge="left">
          <span className="hud-label w-[67px]">GPM/XPM</span>
          <span className="hud-value">
            {view.gpm} / {view.xpm}
          </span>
        </div>,
      )}
      {view.timers.map((timer) =>
        widget(
          timer.id,
          timer.visible,
          <div className="hud-row" data-edge="right" title={timer.name}>
            <img src={RUNE_ICONS[timer.id]} alt={timer.name} className="mr-2 size-[18px]" />
            <span className="hud-value min-w-[38px] text-right">{timer.countdown}</span>
          </div>,
        ),
      )}
      {widget(
        "build",
        view.showBuildPanel && (interactive || view.buildItems.length > 0),
        <div className="hud-panel">
          <div className="mb-2 text-xs font-semibold tracking-[0.08em] text-[var(--hud-accent)]">BUILD</div>
          {view.buildItems.length === 0 ? (
            <div className="text-xs text-[var(--hud-muted)]">Pick a hero to see popular items.</div>
          ) : (
            <ul className="space-y-0.5 text-xs text-[var(--hud-muted)]">
              {view.buildItems.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          )}
        </div>,
      )}
      {interactive && (
        <div
          data-region
          className="absolute top-3 left-1/2 -translate-x-1/2 rounded-md bg-[#1a2430cc] px-3 py-1.5 text-sm text-[var(--hud-accent)]"
        >
          Interactive mode — drag widgets. {view.toggleInteractiveHint} to exit.
        </div>
      )}
    </div>
  );
}

function Widget({ id, interactive, position, screen, onMove, onDrop, children }: {
  id: WidgetId;
  interactive: boolean;
  position: [number, number];
  screen: { width: number; height: number };
  onMove: (p: [number, number]) => void;
  onDrop: (p: [number, number]) => void;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [dragging, setDragging] = useState(false);
  const size = () => ({ width: ref.current?.offsetWidth ?? PLACEMENTS[id].width, height: ref.current?.offsetHeight ?? 22 });
  const [x, y] = clamp(position[0], position[1], size().width, size().height, screen.width, screen.height);

  const onPointerDown = (e: React.PointerEvent) => {
    if (!interactive) return;
    e.preventDefault();
    const start = { x: e.clientX, y: e.clientY, left: x, top: y };
    const { width, height } = size();
    let last: [number, number] = [x, y];
    ref.current?.setPointerCapture(e.pointerId);
    setDragging(true);
    const move = (m: PointerEvent) => {
      last = clamp(start.left + m.clientX - start.x, start.top + m.clientY - start.y, width, height, screen.width, screen.height);
      onMove(last);
    };
    const up = () => {
      setDragging(false);
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      onDrop(last);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };

  return (
    <div
      ref={ref}
      data-region
      className="hud-widget absolute"
      data-interactive={interactive}
      data-dragging={dragging}
      style={{ left: x, top: y, width: PLACEMENTS[id].width }}
      onPointerDown={onPointerDown}
    >
      {children}
    </div>
  );
}

/** Reports widget bounds to Rust so the window is clipped to them and empty screen passes clicks to Dota. */
function useRegions(root: React.RefObject<HTMLDivElement | null>, view: OverlayView | null) {
  const last = useRef("");
  const hasView = view !== null;
  const report = useCallback(() => {
    const el = root.current;
    if (!el || !isTauri) return;
    const rects = [...el.querySelectorAll<HTMLElement>("[data-region]")].map((node) => {
      const r = node.getBoundingClientRect();
      return { x: Math.round(r.left), y: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height) };
    });
    const key = JSON.stringify(rects);
    if (key === last.current) return;
    last.current = key;
    api.overlaySetRegions(rects).catch(() => {});
  }, [root]);

  useLayoutEffect(() => {
    const frame = requestAnimationFrame(report);
    return () => cancelAnimationFrame(frame);
  });

  useEffect(() => {
    const el = root.current;
    if (!el) return;
    const observer = new ResizeObserver(report);
    observer.observe(el);
    document.fonts.addEventListener("loadingdone", report);
    return () => {
      observer.disconnect();
      document.fonts.removeEventListener("loadingdone", report);
    };
  }, [report, root, hasView]);
}
