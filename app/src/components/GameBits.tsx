// Small Dota-specific visuals shared by pages.

import { useState } from "react";
import { POSITION_ICON_PATHS, UNKNOWN_POSITION_PATH } from "../lib/positionIcons";
import { cx } from "./ui";

/** STRATZ-style role icon; a question mark when unknown. */
export function PositionIcon({ position, label, className }: { position: number | null; label: string; className?: string }) {
  const known = position !== null && position >= 1 && position <= 5;
  return (
    <svg
      viewBox={known ? "0 0 24 24" : "0 0 16 16"}
      role="img"
      aria-label={label}
      className={cx("size-4 shrink-0 fill-current text-muted", className)}
    >
      <title>{label}</title>
      <path fillRule={known ? "evenodd" : "nonzero"} d={known ? POSITION_ICON_PATHS[position] : UNKNOWN_POSITION_PATH} />
    </svg>
  );
}

/** Image that falls back to a text label when it fails or is missing. */
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

export function ResultBadge({ result, size = "sm" }: { result: string; size?: "sm" | "xs" }) {
  if (result !== "Win" && result !== "Loss") return null;
  return (
    <span
      className={cx(
        "inline-flex items-center justify-center rounded font-semibold",
        size === "sm" ? "px-1.5 py-0.5 text-[11px]" : "w-full py-px text-[10px]",
        result === "Win" ? "bg-success-bg text-success" : "bg-critical-bg text-critical",
      )}
    >
      {result}
    </span>
  );
}

/** Medal with star overlay; `~` marks an estimate. */
export function RankMedal({ icon, star, estimated, label }: { icon: string; star: string; estimated: boolean; label: string }) {
  if (!icon) return null;
  return (
    <span className="relative inline-block size-9" title={label}>
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
