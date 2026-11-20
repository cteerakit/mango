// Overlay widget placement (C# OverlayWindow PanelBindings).

export type WidgetId = "player" | "bounty" | "power" | "wisdom" | "lotus" | "build";

interface Placement {
  anchor: "left" | "right";
  edgeMargin: number;
  top: number;
  width: number;
}

/** Defaults are measured from a screen edge, so they land correctly on any display width. */
export const PLACEMENTS: Record<WidgetId, Placement> = {
  player: { anchor: "left", edgeMargin: 0, top: 104, width: 164 },
  bounty: { anchor: "right", edgeMargin: 0, top: 64, width: 92 },
  power: { anchor: "right", edgeMargin: 0, top: 86, width: 92 },
  wisdom: { anchor: "right", edgeMargin: 0, top: 108, width: 92 },
  lotus: { anchor: "right", edgeMargin: 0, top: 130, width: 92 },
  build: { anchor: "right", edgeMargin: 114, top: 410, width: 280 },
};

export function defaultPosition(id: WidgetId, screenWidth: number): [number, number] {
  const p = PLACEMENTS[id];
  const x = p.anchor === "left" ? p.edgeMargin : Math.max(0, screenWidth - p.width - p.edgeMargin);
  return [x, p.top];
}

/** Keeps a widget fully on screen. */
export function clamp(x: number, y: number, width: number, height: number, screenWidth: number, screenHeight: number): [number, number] {
  return [Math.min(Math.max(0, x), Math.max(0, screenWidth - width)), Math.min(Math.max(0, y), Math.max(0, screenHeight - height))];
}
