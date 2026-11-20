import { useCallback, useEffect, useRef, useState } from "react";
import { RefreshCw } from "lucide-react";
import { ResultBadge } from "../components/GameBits";
import { Button, Card, Page, Spinner, cx, inputClass } from "../components/ui";
import { api, errorText } from "../lib/api";
import { dash } from "../lib/format";
import { useNotesVersion, useStoreState } from "../lib/hooks";
import type { LocalIdentity, PickerItem, PlayerProfile } from "../lib/types";

/** "You" when `isSelf`; otherwise any player, defaulting to the local user. */
export function PlayerPage({ isSelf = false, steamId: requested }: { isSelf?: boolean; steamId?: string }) {
  const store = useStoreState();
  const notesVersion = useNotesVersion();
  const [picker, setPicker] = useState<PickerItem[]>([]);
  const [localId, setLocalId] = useState("");
  const [selected, setSelected] = useState(requested ?? "");
  const [identity, setIdentity] = useState<LocalIdentity | null>(null);
  const [profile, setProfile] = useState<PlayerProfile | null>(null);
  const [loading, setLoading] = useState(false);
  const loadId = useRef(0);

  // Local account and the picker follow live GSI.
  useEffect(() => {
    api.localPlayer().then(setLocalId).catch(() => {});
    if (!isSelf) api.playerPicker().then(setPicker).catch(() => {});
  }, [isSelf, store.snapshot.steamId, store.snapshot.players.length, notesVersion]);

  const target = isSelf ? localId : selected || localId || picker[0]?.steamId || "";

  const load = useCallback(async (steamId: string) => {
    const id = ++loadId.current;
    setLoading(true);
    setProfile(null);
    try {
      const result = await api.playerProfile(steamId);
      if (id === loadId.current) setProfile(result);
    } catch (error) {
      if (id === loadId.current) setProfile({ overview: null, rankText: "—", error: `Could not load OpenDota data: ${errorText(error)}` });
    } finally {
      if (id === loadId.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (target) load(target);
  }, [target, load]);

  useEffect(() => {
    if (target) api.playerIdentity(target).then(setIdentity).catch(() => {});
    else setIdentity(null);
  }, [target, notesVersion, store.snapshot.players.length]);

  const overview = profile?.overview;
  const name = overview && overview.displayName !== "—" ? overview.displayName : identity?.name;
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
      subtitle={subtitle}
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
              onChange={(e) => setSelected(e.target.value)}
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

      {target && (
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
            {!isSelf && (
              <>
                <dt className="text-muted">Note</dt>
                <dd className="whitespace-pre-wrap">{dash(identity?.note)}</dd>
                <dt className="text-muted">Likes / dislikes</dt>
                <dd className="tabular">
                  {identity?.likes ?? 0} / {identity?.dislikes ?? 0}
                </dd>
              </>
            )}
          </dl>
        </Card>
      )}

      {target && profile && (
        <>
          <Card className="p-5">
            <div className="flex items-center justify-between">
              <h2 className="font-display text-base font-semibold">Overall</h2>
              {profile.error && (
                <Button onClick={() => load(target)} disabled={loading}>
                  <RefreshCw className="size-4" /> Retry OpenDota
                </Button>
              )}
            </div>
            <div className="mt-4 grid grid-cols-2 gap-4 sm:grid-cols-5">
              <Stat label="Rank" value={profile.rankText} wide />
              <Stat label="Wins" value={overview ? String(overview.wins) : "—"} />
              <Stat label="Losses" value={overview ? String(overview.losses) : "—"} />
              <Stat label="Win rate" value={overview?.winRatePercent != null ? `${overview.winRatePercent}%` : "—"} />
              <Stat label="Total matches" value={overview ? (overview.wins + overview.losses).toLocaleString() : "—"} />
            </div>
            <p className="mt-3 text-xs text-faint">Data source: OpenDota public match history, including Turbo.</p>
          </Card>

          <Card className="overflow-hidden">
            <h2 className="px-5 pt-5 font-display text-base font-semibold">Previous matches</h2>
            {!overview || overview.recentMatches.length === 0 ? (
              <p className="px-5 py-4 text-sm text-muted">{profile.error ?? "No recent matches found."}</p>
            ) : (
              <table className="mt-3 w-full text-sm">
                <thead className="border-y border-line bg-surface-2 text-left text-xs tracking-wide text-muted uppercase">
                  <tr>
                    {["Hero", "Result", "KDA", "Time", "Played", "Match ID"].map((h) => (
                      <th key={h} className="px-5 py-2 font-semibold">
                        {h}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {overview.recentMatches.map((m) => (
                    <tr key={m.matchId} className="border-b border-line last:border-b-0">
                      <td className="px-5 py-2 font-medium">{m.heroName}</td>
                      <td className="px-5 py-2">
                        <ResultBadge result={m.result} />
                      </td>
                      <td className="tabular px-5 py-2">{m.kda}</td>
                      <td className="tabular px-5 py-2">{m.duration}</td>
                      <td className="px-5 py-2 text-muted">{m.playedAt}</td>
                      <td className="tabular px-5 py-2 text-muted select-text">{m.matchId}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </Card>
        </>
      )}
    </Page>
  );
}

function Stat({ label, value, wide }: { label: string; value: string; wide?: boolean }) {
  return (
    <div className={cx("rounded-lg bg-surface-2 px-4 py-3", wide && "col-span-2 sm:col-span-1")}>
      <div className="text-xs text-muted">{label}</div>
      <div className="mt-0.5 truncate font-display text-lg font-semibold" title={value}>
        {value}
      </div>
    </div>
  );
}
