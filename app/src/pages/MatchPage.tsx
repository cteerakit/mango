import { useCallback, useEffect, useRef, useState } from "react";
import { Lock, MessageSquareText, MessageSquarePlus, ThumbsDown, ThumbsUp, TriangleAlert } from "lucide-react";
import type { Navigate } from "../App";
import { GameImage, PositionIcon, RankMedal, ResultBadge } from "../components/GameBits";
import { Button, Card, Dialog, IconToggle, Notice, cx, inputClass } from "../components/ui";
import { api, errorText, openUrl } from "../lib/api";
import { positionLabel, purchaseTime, relativeTime, splitTeams, upper } from "../lib/format";
import { useMatchView, useNow } from "../lib/hooks";
import type { MatchBuildItem, PlayerRow, RankOption } from "../lib/types";

const VISIBLE_ITEMS = 11;
// Column template shared by the header and rows: player | matches | last match | recent | actions.
const COLUMNS = "grid grid-cols-[288px_112px_632px_272px_180px] items-center gap-x-3";

type Status = { tone: "success" | "critical"; text: string } | null;

export function MatchPage({ navigate }: { navigate: Navigate }) {
  const view = useMatchView();
  const [noteFor, setNoteFor] = useState<PlayerRow | null>(null);
  const [rankFor, setRankFor] = useState<PlayerRow | null>(null);
  const [status, setStatus] = useState<Status>(null);
  const teams = splitTeams(view.players);

  const act = useCallback(async (work: () => Promise<unknown>) => {
    try {
      await work();
    } catch (error) {
      setStatus({ tone: "critical", text: errorText(error) });
    }
  }, []);

  const handlers: RowHandlers = {
    openPlayer: (p) => p.steamId && navigate({ page: "player", steamId: p.steamId }),
    vote: (p, vote) => act(() => api.noteVote(p.name, p.steamId, vote)),
    toggleToxic: (p) => act(() => api.noteToggleToxic(p.name, p.steamId)),
    editNote: setNoteFor,
    editRank: setRankFor,
  };

  return (
    <div className="h-full overflow-y-auto">
      <div className="space-y-5 px-8 py-7">
        <header>
          <h1 className="font-display text-2xl font-semibold tracking-tight">Match</h1>
          <p className="mt-1 text-sm text-muted">{view.subtitle}</p>
        </header>
        {status && (
          <Notice tone={status.tone} onClose={() => setStatus(null)}>
            {status.text}
          </Notice>
        )}
        <TeamCard title="The Radiant" accent="bg-radiant" players={teams.radiant} handlers={handlers} />
        <TeamCard title="The Dire" accent="bg-dire" players={teams.dire} handlers={handlers} />
        <TeamCard title="Lobby" accent="bg-faint" players={teams.lobby} handlers={handlers} />
      </div>
      <NoteDialog player={noteFor} onClose={() => setNoteFor(null)} onError={(text) => setStatus({ tone: "critical", text })} />
      <RankDialog
        player={rankFor}
        onClose={() => setRankFor(null)}
        onResult={(tone, text) => setStatus({ tone, text })}
      />
    </div>
  );
}

interface RowHandlers {
  openPlayer: (p: PlayerRow) => void;
  vote: (p: PlayerRow, vote: "like" | "dislike") => void;
  toggleToxic: (p: PlayerRow) => void;
  editNote: (p: PlayerRow) => void;
  editRank: (p: PlayerRow) => void;
}

function TeamCard({ title, accent, players, handlers }: { title: string; accent: string; players: PlayerRow[]; handlers: RowHandlers }) {
  if (players.length === 0) return null;
  return (
    <Card className="overflow-hidden">
      <div className="overflow-x-auto">
        <div className="min-w-max">
          <div className={cx(COLUMNS, "border-b border-line bg-surface-2 px-3 py-2")}>
            <div className="sticky left-0 z-10 flex items-center gap-2.5 bg-surface-2">
              <span className={cx("h-8 w-1 rounded-full", accent)} />
              <h2 className="font-display text-base font-semibold">{title}</h2>
            </div>
            <ColumnHeader title="Matches recorded by OpenDota, including Turbo (wins + losses)">Matches</ColumnHeader>
            <ColumnHeader title="Result, KDA, position and items from this player's latest completed match on this hero">
              Last match as this hero
            </ColumnHeader>
            <ColumnHeader title="Last 5 completed matches, newest first">Recent matches</ColumnHeader>
            <div className="sticky right-0 z-10 h-full border-l border-line bg-surface-2 pl-3">
              <ColumnHeader>Actions</ColumnHeader>
            </div>
          </div>
          {players.map((player) => (
            <PlayerRowView key={`${player.steamId}|${player.name}|${player.teamSlot}`} player={player} handlers={handlers} />
          ))}
        </div>
      </div>
    </Card>
  );
}

function ColumnHeader({ children, title }: { children: string; title?: string }) {
  return (
    <div title={title} className="flex h-full items-center text-xs font-semibold tracking-wide text-muted uppercase">
      {children}
    </div>
  );
}

function PlayerRowView({ player, handlers }: { player: PlayerRow; handlers: RowHandlers }) {
  useNow(60_000);
  const history = player.history;
  const liked = player.currentMatchVote === "like";
  const disliked = player.currentMatchVote === "dislike";
  const rowBg = player.isLocalPlayer ? "bg-[color-mix(in_srgb,var(--accent)_7%,var(--surface))]" : "bg-surface";
  const noteLabel = player.note ? `Edit note: ${player.note}` : "Add note";
  const voteTitle = (base: string, on: boolean) => (!player.canVote ? player.voteUnavailableReason : on ? `Remove ${base.toLowerCase()}` : base);

  return (
    <div className={cx(COLUMNS, "border-b border-line px-3 py-2 last:border-b-0", rowBg)}>
      {/* Player */}
      <div className={cx("sticky left-0 z-10 flex min-w-0 items-center gap-3", rowBg)}>
        <GameImage
          src={player.heroImageUrl}
          alt={player.displayHeroName || "Unknown hero"}
          fallback={player.displayHeroName || "?"}
          className="h-9 w-16 shrink-0 rounded"
        />
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 items-center gap-1.5">
            <button
              type="button"
              onClick={() => handlers.openPlayer(player)}
              disabled={!player.steamId}
              title={player.name}
              className="truncate font-semibold hover:text-accent hover:underline disabled:no-underline disabled:hover:text-fg"
            >
              {player.name || "Unknown"}
            </button>
            {player.isProfilePrivate && (
              <span title="Dota match history unavailable" className="shrink-0">
                <Lock className="size-3.5 text-muted" aria-label="Dota match history unavailable" />
              </span>
            )}
          </div>
          <div className="truncate text-[11px] text-muted">{upper(player.displayHeroName)}</div>
        </div>
        <RankCell player={player} onEdit={() => handlers.editRank(player)} />
      </div>

      {/* Matches */}
      <div className="tabular text-sm" title="Matches recorded by OpenDota, including Turbo (wins + losses)">
        {player.matchesLabel}
      </div>

      {/* Last match as this hero */}
      <div className="grid grid-cols-[150px_1fr] items-center gap-3" title={history.heroMatchLabel}>
        <div className="space-y-0.5">
          <div className="flex items-center gap-1.5">
            <ResultBadge result={history.result} />
            {history.heroMatchUrl && (
              <PositionIcon
                position={history.position}
                label={`${positionLabel(history.position, history.isPositionEstimated)}\n${history.positionBasis}`}
              />
            )}
            <span className="tabular text-base">{history.kda}</span>
          </div>
          {history.heroMatchUrl && (
            <button type="button" onClick={() => openUrl(history.heroMatchUrl)} className="text-xs text-accent hover:underline">
              {relativeTime(history.playedAt) || "View match"}
            </button>
          )}
        </div>
        <ItemStrip items={history.items} />
      </div>

      {/* Recent matches */}
      <div>
        {player.isProfilePrivate ? (
          <span className="text-muted">—</span>
        ) : history.recentMatches.length === 0 ? (
          <span className="text-xs text-muted">{history.recentMatchesLabel}</span>
        ) : (
          <div className="flex gap-1.5" title={history.recentMatchesLabel}>
            {history.recentMatches.map((m, i) => (
              <div key={i} className="w-12 space-y-0.5" title={`${m.heroName} · ${m.result}`}>
                <GameImage src={m.heroImageUrl} alt={m.heroName} className="h-[27px] w-12 rounded-sm" />
                <ResultBadge result={m.result} size="xs" />
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Actions */}
      <div className={cx("sticky right-0 z-10 flex h-full items-center gap-2 border-l border-line pl-3", rowBg)}>
        <IconToggle label={voteTitle("Like", liked)} pressed={liked} tone="success" disabled={!player.canVote} onClick={() => handlers.vote(player, "like")}>
          <ThumbsUp className="size-4" fill={liked ? "currentColor" : "none"} />
        </IconToggle>
        <IconToggle label={voteTitle("Dislike", disliked)} pressed={disliked} tone="critical" disabled={!player.canVote} onClick={() => handlers.vote(player, "dislike")}>
          <ThumbsDown className="size-4" fill={disliked ? "currentColor" : "none"} />
        </IconToggle>
        <IconToggle
          label={!player.canMarkToxic ? "Player has no ID." : player.isToxic ? "Remove toxic mark" : "Mark as toxic player"}
          pressed={player.isToxic}
          tone="caution"
          disabled={!player.canMarkToxic}
          onClick={() => handlers.toggleToxic(player)}
        >
          <TriangleAlert className="size-4" />
        </IconToggle>
        <IconToggle label={noteLabel} pressed={!!player.note} disabled={!player.steamId} onClick={() => handlers.editNote(player)}>
          {player.note ? <MessageSquareText className="size-4" /> : <MessageSquarePlus className="size-4" />}
        </IconToggle>
      </div>
    </div>
  );
}

function RankCell({ player, onEdit }: { player: PlayerRow; onEdit: () => void }) {
  const medal = <RankMedal icon={player.rankIconUrl} star={player.rankStarUrl} estimated={player.rankIsEstimated} label={player.rankLabel} />;
  if (!player.canEditRank) return <span className="size-9 shrink-0">{medal}</span>;
  return (
    <button
      type="button"
      onClick={onEdit}
      title={player.rankLabel ? `${player.rankLabel} — set or clear a manual rank` : "Set a manual rank"}
      aria-label="Edit player rank"
      className="flex size-10 shrink-0 items-center justify-center rounded-md border border-dashed border-line text-xl text-muted hover:border-accent hover:text-fg"
    >
      {player.rankIconUrl ? medal : "+"}
    </button>
  );
}

function ItemTile({ item }: { item: MatchBuildItem }) {
  const tooltip = `${item.name} · ${item.slot} · ${purchaseTime(item.purchaseTimeSeconds)}`;
  return (
    <div className="w-9 space-y-0.5" title={tooltip}>
      <GameImage src={item.imageUrl} alt={item.name} className="h-[26px] w-9 rounded" />
      <div className="tabular text-center text-[11px] font-semibold text-muted">{purchaseTime(item.purchaseTimeSeconds)}</div>
    </div>
  );
}

function ItemStrip({ items }: { items: MatchBuildItem[] }) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => !box.current?.contains(e.target as Node) && setOpen(false);
    window.addEventListener("mousedown", close);
    return () => window.removeEventListener("mousedown", close);
  }, [open]);
  const visible = items.slice(0, VISIBLE_ITEMS);
  const overflow = items.slice(VISIBLE_ITEMS);
  return (
    <div ref={box} className="relative flex items-start gap-1">
      {visible.map((item, i) => (
        <ItemTile key={i} item={item} />
      ))}
      {overflow.length > 0 && (
        <button
          type="button"
          aria-label="Show more item purchases"
          onClick={() => setOpen(!open)}
          className="h-[26px] w-8 rounded border border-line bg-surface-2 text-xs font-semibold hover:bg-surface-3"
        >
          +{overflow.length}
        </button>
      )}
      {open && (
        <div className="absolute top-9 right-0 z-30 flex max-w-[480px] gap-1 overflow-x-auto rounded-lg border border-line bg-surface p-2 shadow-xl">
          {overflow.map((item, i) => (
            <ItemTile key={i} item={item} />
          ))}
        </div>
      )}
    </div>
  );
}

function NoteDialog({ player, onClose, onError }: { player: PlayerRow | null; onClose: () => void; onError: (text: string) => void }) {
  const [text, setText] = useState("");
  useEffect(() => setText(player?.note ?? ""), [player]);
  if (!player) return null;
  const save = async (content: string) => {
    try {
      await api.noteSave(player.name, player.steamId, content.trim());
      onClose();
    } catch (error) {
      onError(errorText(error));
    }
  };
  return (
    <Dialog
      open
      title={player.name ? `Note — ${player.name}` : "Player note"}
      onClose={onClose}
      footer={
        <>
          {player.note && (
            <Button variant="ghost" className="mr-auto" onClick={() => save("")}>
              Clear
            </Button>
          )}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={() => save(text)}>
            Save
          </Button>
        </>
      }
    >
      <textarea
        aria-label="Note"
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => e.key === "Enter" && e.ctrlKey && save(text)}
        className={cx(inputClass, "h-36 resize-none py-2")}
      />
      <p className="mt-2 text-xs text-muted">Notes are matched by Steam ID, so they follow players who change their name.</p>
    </Dialog>
  );
}

function RankDialog({ player, onClose, onResult }: {
  player: PlayerRow | null;
  onClose: () => void;
  onResult: (tone: "success" | "critical", text: string) => void;
}) {
  const [options, setOptions] = useState<RankOption[]>([]);
  const [tier, setTier] = useState<number | null>(null);
  useEffect(() => {
    api.rankOptions().then(setOptions).catch(() => {});
  }, []);
  useEffect(() => setTier(null), [player]);
  if (!player) return null;
  const hasSaved = player.rankLabel.startsWith("Manual:");
  const apply = async (value: number | null) => {
    try {
      onResult("success", await api.rankSet(player.steamId, player.name, value));
    } catch (error) {
      onResult("critical", errorText(error));
    }
    onClose();
  };
  return (
    <Dialog
      open
      title={player.name ? `Rank — ${player.name}` : "Player rank"}
      onClose={onClose}
      footer={
        <>
          {hasSaved && (
            <Button variant="ghost" className="mr-auto" onClick={() => apply(null)}>
              Clear override
            </Button>
          )}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={tier === null} onClick={() => apply(tier)}>
            Save rank
          </Button>
        </>
      }
    >
      <p className="mb-3 text-muted">
        Save a manual rank for a private profile or a player with no reported rank, or clear the saved rank.
      </p>
      <label className="mb-1.5 block font-medium" htmlFor="manual-rank">
        Manual rank
      </label>
      <select
        id="manual-rank"
        value={tier ?? ""}
        onChange={(e) => setTier(e.target.value ? Number(e.target.value) : null)}
        className={inputClass}
      >
        <option value="">Choose a rank</option>
        {options.map((o) => (
          <option key={o.tier} value={o.tier}>
            {o.label}
          </option>
        ))}
      </select>
    </Dialog>
  );
}
