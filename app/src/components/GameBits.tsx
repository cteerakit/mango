// Small Dota-specific visuals shared by pages.

import { useState } from "react";
import type { MatchAnalysisOutcome } from "../lib/types";
import { cx } from "./ui";

const UNKNOWN_POSITION_PATH = "M 2,4 C 2,-1 14,-1 14,5 C 14,8 10,9 9,11 L 6,11 C 6,7 11,7 11,4 C 11,2 5,2 5,4 Z M 6,13 L 9,13 L 9,16 L 6,16 Z";

/** STRATZ-style role icon; a question mark when unknown. */
export function PositionIcon({ position, label, className }: { position: number | null; label: string; className?: string }) {
  if (position !== null && Number.isInteger(position) && position >= 1 && position <= 5) {
    return <img src={`/positions/${position}.svg`} alt={label} title={label} className={cx("size-4 shrink-0", className)} />;
  }
  return (
    <svg
      viewBox="0 0 16 16"
      role="img"
      aria-label={label}
      className={cx("size-4 shrink-0 fill-current text-muted", className)}
    >
      <title>{label}</title>
      <path d={UNKNOWN_POSITION_PATH} />
    </svg>
  );
}

/** Image that falls back to a text label when it fails or is missing. */
export function SmurfBadge() {
  return (
    <span
      className="inline-block whitespace-nowrap rounded border border-amber-500/40 bg-amber-500/15 px-1.5 py-0.5 text-xs font-medium text-amber-300"
      title="50–500 matches recorded by OpenDota (including Turbo) with a win rate of at least 65%"
    >
      Possible smurf
    </span>
  );
}

export function GameImage({ src, alt, className, fallback }: { src: string; alt: string; className?: string; fallback?: string }) {
  const [failed, setFailed] = useState(false);
  if (!src || failed) {
    return (
      <span
        className={cx("flex items-center justify-center overflow-hidden bg-surface-3 text-[9px] leading-tight text-muted", className)}
        title={alt}
      >
        <span className="line-clamp-2 px-0.5 text-center">{fallback ?? alt}</span>
      </span>
    );
  }
  return <img src={src} alt={alt} title={alt} loading="lazy" className={cx("object-cover", className)} onError={() => setFailed(true)} />;
}

// Icon shapes from https://medium.com/stratz/keep-on-role-ing-1af92088182a.
const ANALYSIS_OUTCOMES = {
  stomp: { label: "Stomp", icon: "/match-outcomes/stomp.png" },
  comeback: { label: "Comeback", icon: "/match-outcomes/comeback.png" },
  closeGame: { label: "Close game", icon: "/match-outcomes/close-game.png" },
};

export function ResultBadge({ result, size = "sm", analysisOutcome }: { result: string; size?: "sm" | "xs"; analysisOutcome?: MatchAnalysisOutcome | null }) {
  if (result !== "Win" && result !== "Loss") return null;
  const analysis = analysisOutcome ? ANALYSIS_OUTCOMES[analysisOutcome] : null;
  return (
    <span
      className={cx(
        "inline-flex items-center justify-center gap-1 rounded font-semibold",
        size === "sm" ? "px-1.5 py-0.5 text-[11px]" : "w-full py-px text-[10px]",
        result === "Win" ? "bg-success-bg text-success" : "bg-critical-bg text-critical",
      )}
    >
      {result}
      {analysis && (
        <span
          className={cx("shrink-0 bg-current mask-contain mask-center mask-no-repeat", size === "xs" ? "size-3" : "size-3.5")}
          style={{ maskImage: `url(${analysis.icon})` }}
          role="img"
          aria-label={analysis.label}
          title={analysis.label}
        />
      )}
    </span>
  );
}

/** Medal with star overlay; `~` marks an estimate. */
export function RankMedal({ icon, star, estimated, label, className }: { icon: string; star: string; estimated: boolean; label: string; className?: string }) {
  if (!icon) return null;
  return (
    <span className={cx("relative inline-block", className ?? "size-9")} title={label}>
      <img src={icon} alt={label} className="absolute inset-0 size-full object-contain" />
      {star && <img src={star} alt="" className="absolute inset-0 size-full object-contain" />}
      {estimated && (
        <span className="absolute -right-0.5 -bottom-0.5 rounded border border-caution bg-caution-bg px-1 text-[10px] leading-3.5 font-semibold text-caution">
          ~
        </span>
      )}
    </span>
  );
}
