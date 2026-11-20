// Display text for the UI (the C# models' computed properties).

export function relativeTime(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "";
  const seconds = (now - Date.parse(iso)) / 1000;
  const minutes = seconds / 60;
  const hours = minutes / 60;
  const days = hours / 24;
  const ago = (n: number, unit: string) => `${Math.trunc(n)} ${unit}${Math.trunc(n) === 1 ? "" : "s"} ago`;
  if (minutes < 1) return "Just now";
  if (hours < 1) return ago(minutes, "minute");
  if (days < 1) return ago(hours, "hour");
  if (days < 30) return ago(days, "day");
  if (days < 365) return ago(days / 30, "month");
  return ago(days / 365, "year");
}

export function purchaseTime(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return "—";
  const abs = Math.abs(seconds);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${seconds < 0 ? "−" : ""}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

const ROLES: Record<number, string> = {
  1: "Pos 1 · Carry",
  2: "Pos 2 · Mid",
  3: "Pos 3 · Offlane",
  4: "Pos 4 · Soft support",
  5: "Pos 5 · Hard support",
};

export function positionLabel(position: number | null | undefined, estimated = true): string {
  const role = position ? ROLES[position] : undefined;
  if (!role) return "Position unavailable";
  return estimated ? `${role} (estimated)` : role;
}

export function upper(text: string): string {
  return text.toLocaleUpperCase();
}

export function dash(text: string | null | undefined): string {
  return text && text.trim() ? text : "—";
}

export function shortDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" });
}

/** Teams in display order. Unknown sides go to the lobby. */
export function splitTeams<T extends { teamName: string }>(players: T[]) {
  const side = (p: T) => p.teamName.toLowerCase();
  return {
    radiant: players.filter((p) => side(p).includes("radiant")),
    dire: players.filter((p) => side(p).includes("dire")),
    lobby: players.filter((p) => !side(p).includes("radiant") && !side(p).includes("dire")),
  };
}
