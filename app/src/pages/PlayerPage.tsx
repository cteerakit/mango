import { useCallback, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { RefreshCw, UserRound } from "lucide-react";
import { GameImage, RankMedal, ResultBadge, SmurfBadge } from "../components/GameBits";
import { Button, Card, Page, ScrollableTable, Spinner, cx, inputClass } from "../components/ui";
import { api, errorText } from "../lib/api";
import { dash } from "../lib/format";
import { useNotesVersion, useStoreState } from "../lib/hooks";
import type { LocalIdentity, PickerItem, PlayerProfile } from "../lib/types";

const PROFILE_MAX_AGE = 15 * 60_000;
const PROFILE_ERROR_MAX_AGE = 30_000;

type CachedProfile = { version: number; loadedAt: number; profile: PlayerProfile };

/** "You" when `isSelf`; otherwise any player, defaulting to the local user. */
export function PlayerPage({ isSelf = false, steamId: requested, active = true }: { isSelf?: boolean; steamId?: string; active?: boolean }) {
  const store = useStoreState();
  const notesVersion = useNotesVersion();
  const [picker, setPicker] = useState<PickerItem[]>([]);
  const [localAccount, setLocalAccount] = useState<{ mock: boolean; id: string }>();
  const [selection, setSelection] = useState<{ requested: string | undefined; id: string }>();
  const [playerIdentity, setPlayerIdentity] = useState<{ key: string; identity: LocalIdentity }>();
  const [cached, setCached] = useState(() => new Map<string, CachedProfile>());
  const [pending, setPending] = useState(() => new Set<string>());
  const requests = useRef(new Map<string, { version: number }>());
  const mock = store.isUsingMockData;

  // Local account and the picker follow live GSI.
  useEffect(() => {
    let current = true;
    api.localPlayer().then((id) => {
      if (current) setLocalAccount({ mock, id });
    }).catch(() => {});
    return () => { current = false; };
  }, [mock, store.pageSnapshot.steamId, store.snapshot.steamId, store.snapshot.players.length, notesVersion]);

  useEffect(() => {
    if (isSelf || !active) return;
    let current = true;
    api.playerPicker().then((items) => {
      if (current) setPicker(items);
    }).catch(() => {});
    return () => { current = false; };
  }, [active, isSelf, mock, store.pageSnapshot.steamId, store.snapshot.steamId, store.snapshot.players.length, notesVersion]);

  const localId = localAccount?.mock === mock ? localAccount.id : "";
  const selected = selection && selection.requested === requested ? selection.id : requested;
  const target = isSelf ? localId : selected || localId || picker[0]?.steamId || "";
  const key = `${mock}:${target}`;
  const entry = cached.get(key);
  const profile = entry?.profile ?? null;
  const loading = pending.has(key);
  const identity = playerIdentity?.key === key ? playerIdentity.identity : undefined;

  const load = useCallback(async () => {
    if (!target || requests.current.get(key)?.version === notesVersion) return;
    const next = { version: notesVersion };
    requests.current.set(key, next);
    setPending((previous) => new Set(previous).add(key));
    let result: PlayerProfile;
    try {
      result = await api.playerProfile(target);
    } catch (error) {
      result = { overview: null, rankMedal: null, error: `Could not load OpenDota data: ${errorText(error)}` };
    }
    if (requests.current.get(key) !== next) return;
    requests.current.delete(key);
    setPending((previous) => {
      const remaining = new Set(previous);
      remaining.delete(key);
      return remaining;
    });
    const loadedAt = Date.now();
    setCached((previous) => {
      const existing = previous.get(key)?.profile;
      return new Map(previous).set(key, {
        version: next.version,
        loadedAt,
        profile: result.error && existing?.overview ? { ...existing, error: result.error } : result,
      });
    });
  }, [key, notesVersion, target]);

  useEffect(() => {
    if (!active || !target) return;
    const maxAge = entry?.profile.error ? PROFILE_ERROR_MAX_AGE : PROFILE_MAX_AGE;
    if (!entry || entry.version !== notesVersion || Date.now() - entry.loadedAt >= maxAge) void load();
  }, [active, entry, load, notesVersion, target]);

  useEffect(() => {
    let current = true;
    if (!active || !target) return;
    api.playerIdentity(target).then((next) => {
      if (current) setPlayerIdentity({ key, identity: next });
    }).catch(() => {});
    return () => { current = false; };
  }, [active, key, target, notesVersion, store.snapshot.players.length]);

  const overview = profile?.overview;
  const name = overview && overview.displayName !== "—" ? overview.displayName : identity?.name;
  const totalMatches = overview ? overview.wins + overview.losses : null;
  const subtitle = !target
    ? isSelf
      ? "Launch Dota 2 and enter a match once to detect your account. Your stats will appear here automatically."
      : "No Steam ID found. Launch Dota 2 and connect to a match, or click a player name on the Match page to open them here."
    : profile?.error
      ? profile.error
      : overview
        ? isSelf
          ? "Your stats and recent matches, including Turbo."
          : `Overall stats and recent matches for ${overview.displayName}.`
        : "Overall stats and recent matches from OpenDota.";

  return (
    <Page
      title={isSelf ? "You" : "Player"}
      subtitle={isSelf && target ? undefined : subtitle}
      actions={loading ? <Spinner className="size-5" /> : undefined}
    >
      {!isSelf && (
        <Card className="p-5">
          <label htmlFor="player-picker" className="mb-1.5 block text-sm font-medium">
            Select player
          </label>
          {picker.length === 0 ? (
            <p className="text-sm text-muted">
              No players yet. Connect Dota 2 GSI, or open a player from the Match page to populate this list.
            </p>
          ) : (
            <select
              id="player-picker"
              className={inputClass}
              value={target}
              disabled={loading}
              onChange={(e) => setSelection({ requested, id: e.target.value })}
            >
              {!picker.some((p) => p.steamId === target) && target && <option value={target}>{target}</option>}
              {picker.map((p) => (
                <option key={p.steamId} value={p.steamId}>
                  {p.name ? `${p.name} · ${p.steamId}` : p.steamId}
                </option>
              ))}
            </select>
          )}
        </Card>
      )}

      {target && isSelf && (
        <Card className="overflow-hidden">
          <div className="flex items-center justify-between gap-4 bg-linear-to-br from-local via-surface to-surface px-5 py-5 sm:px-6">
            <div className="flex min-w-0 items-center gap-4">
              <div className="grid size-16 shrink-0 place-items-center overflow-hidden rounded-xl border border-line bg-surface-2 text-muted">
                {overview?.avatarUrl ? (
                  <img src={overview.avatarUrl} alt="" className="size-full object-cover" />
                ) : (
                  <UserRound aria-hidden="true" className="size-7" />
                )}
              </div>
              <div className="min-w-0">
                <h2 className="truncate font-display text-2xl font-semibold tracking-tight">{name || "Steam profile"}</h2>
                {overview?.possibleSmurf && <SmurfBadge />}
                <p className="mt-1 flex flex-wrap gap-x-2 text-xs text-muted">
                  <span>Steam ID</span>
                  <span className="tabular select-text">{target}</span>
                </p>
              </div>
            </div>
            <div className="flex shrink-0 flex-col items-center gap-0.5 border-l border-line pl-4 sm:pl-6">
              {profile?.rankMedal ? <RankMedal {...profile.rankMedal} className="size-14" /> : <span className="grid size-14 place-items-center text-muted">—</span>}
              <span className="text-xs text-muted">Rank</span>
            </div>
          </div>
          {overview && (
            <dl className="grid grid-cols-2 gap-px border-t border-line bg-line sm:grid-cols-4">
              {[
                { label: "Win rate", value: overview.winRatePercent == null ? "—" : `${overview.winRatePercent}%`, tone: "text-accent-strong" },
                { label: "Wins", value: overview.wins.toLocaleString(), tone: "text-fg" },
                { label: "Losses", value: overview.losses.toLocaleString(), tone: "text-fg" },
                { label: "Total matches", value: totalMatches?.toLocaleString(), tone: "text-fg" },
              ].map(({ label, value, tone }) => (
                <div key={label} className="bg-surface px-5 py-3.5 sm:px-6">
                  <dt className="text-xs text-muted">{label}</dt>
                  <dd className={cx("tabular mt-1 font-display text-xl font-semibold tracking-tight", tone)}>{value}</dd>
                </div>
              ))}
            </dl>
          )}
        </Card>
      )}

      {target && !isSelf && (
        <Card className="flex items-center gap-5 p-5">
          {overview?.avatarUrl ? (
            <img src={overview.avatarUrl} alt="" className="size-16 rounded-lg" />
          ) : (
            <div className="size-16 rounded-lg bg-surface-3" />
          )}
          <dl className="grid flex-1 grid-cols-[auto_1fr] gap-x-6 gap-y-1 text-sm">
            <dt className="text-muted">Name</dt>
            <dd className="font-semibold">{dash(name)}</dd>
            <dt className="text-muted">Steam ID</dt>
            <dd className="tabular select-text">{target}</dd>
            <dt className="text-muted">Note</dt>
            <dd className="whitespace-pre-wrap">{dash(identity?.note)}</dd>
            <dt className="text-muted">Likes / dislikes</dt>
            <dd className="tabular">
              {identity?.likes ?? 0} / {identity?.dislikes ?? 0}
            </dd>
          </dl>
        </Card>
      )}

      {target && profile && (
        <>
          {!isSelf && (
            <Card className="p-5">
              <div className="flex items-center justify-between">
                <div className="flex flex-wrap items-center gap-2">
                  <h2 className="font-display text-base font-semibold">Overall</h2>
                  {overview?.possibleSmurf && <SmurfBadge />}
                </div>
                {profile.error && (
                  <Button onClick={() => void load()} disabled={loading}>
                    <RefreshCw className="size-4" /> Retry OpenDota
                  </Button>
                )}
              </div>
              <div className="mt-4 grid grid-cols-2 gap-4 sm:grid-cols-5">
                <Stat label="Rank" value={profile.rankMedal ? <RankMedal {...profile.rankMedal} /> : "—"} wide />
                <Stat label="Wins" value={overview ? String(overview.wins) : "—"} />
                <Stat label="Losses" value={overview ? String(overview.losses) : "—"} />
                <Stat label="Win rate" value={overview?.winRatePercent != null ? `${overview.winRatePercent}%` : "—"} />
                <Stat label="Total matches" value={overview ? (overview.wins + overview.losses).toLocaleString() : "—"} />
              </div>
              <p className="mt-3 text-xs text-faint">Data source: OpenDota public match history, including Turbo.</p>
            </Card>
          )}

          <Card className="overflow-hidden">
            <div className={cx("flex flex-wrap items-center justify-between gap-3 px-5", isSelf ? "py-4 sm:px-6" : "pt-5")}>
              <div>
                <h2 className={cx("font-display font-semibold", isSelf ? "text-lg tracking-tight" : "text-base")}>Previous matches</h2>
                {isSelf && <p className="mt-0.5 text-xs text-muted">OpenDota public match history, including Turbo</p>}
              </div>
              {isSelf && profile.error && (
                <Button onClick={() => void load()} disabled={loading}>
                  <RefreshCw className="size-4" /> Retry OpenDota
                </Button>
              )}
            </div>
            {isSelf && profile.error && overview && overview.recentMatches.length > 0 && (
              <p role="status" className="px-5 pb-4 text-sm text-muted sm:px-6">Refresh failed. Showing the last loaded matches.</p>
            )}
            {!overview || overview.recentMatches.length === 0 ? (
              <p className="px-5 py-4 text-sm text-muted">{profile.error ?? "No recent matches found."}</p>
            ) : (
              <ScrollableTable>
                <table className={cx("w-full text-sm", !isSelf && "mt-3")}>
                  <thead className="border-y border-line bg-surface-2 text-left text-xs tracking-wide text-muted uppercase">
                    <tr>
                      {["Hero", "Result", "KDA", "Time", "Played", "Match ID"].map((h) => (
                        <th key={h} scope="col" className={cx("whitespace-nowrap py-2 font-semibold", isSelf ? "px-3 first:pl-5 last:pr-5 sm:first:pl-6 sm:last:pr-6" : "px-5")}>
                          {h}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {overview.recentMatches.map((m) => (
                      <tr key={m.matchId} className={cx("border-b border-line last:border-b-0", isSelf && "hover:bg-surface-2")}>
                        <td className={cx("font-medium", isSelf ? "py-2.5 pr-3 pl-5 sm:pl-6" : "px-5 py-2")}>
                          <div className={cx("flex items-center gap-2", isSelf && "min-w-32")}>
                            <GameImage src={m.heroImageUrl} alt="" className={cx("shrink-0", isSelf ? "h-8 w-14 rounded-md" : "h-[27px] w-12 rounded-sm")} />
                            {m.heroName}
                          </div>
                        </td>
                        <td className={cx("py-2", isSelf ? "px-3" : "px-5")}>
                          <ResultBadge result={m.result} analysisOutcome={m.analysisOutcome} />
                        </td>
                        <td className={cx("tabular py-2", isSelf ? "px-3" : "px-5")}>{m.kda}</td>
                        <td className={cx("tabular py-2", isSelf ? "px-3" : "px-5")}>{m.duration}</td>
                        <td className={cx("py-2 text-muted", isSelf ? "px-3" : "px-5")}>{m.playedAt}</td>
                        <td className={cx("tabular py-2 text-muted select-text", isSelf ? "pr-5 pl-3 sm:pr-6" : "px-5")}>{m.matchId}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </ScrollableTable>
            )}
          </Card>
        </>
      )}
    </Page>
  );
}

function Stat({ label, value, wide }: { label: string; value: ReactNode; wide?: boolean }) {
  return (
    <div className={cx("rounded-lg bg-surface-2 px-4 py-3", wide && "col-span-2 sm:col-span-1")}>
      <div className="text-xs text-muted">{label}</div>
      <div
        className={cx("mt-0.5 font-display text-lg font-semibold", typeof value === "string" && "truncate")}
        title={typeof value === "string" ? value : undefined}
      >
        {value}
      </div>
    </div>
  );
}
