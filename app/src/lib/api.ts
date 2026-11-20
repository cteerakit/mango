// Typed wrappers over the Rust commands. Outside Tauri (plain `npm run vite` in a browser)
// calls resolve to empty defaults so pages still render for layout work.

import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import type * as T from "./types";

export const isTauri = typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

async function invoke<R>(command: string, args?: Record<string, unknown>, fallback?: R): Promise<R> {
  if (!isTauri) {
    if (fallback === undefined) throw new Error(`${command} needs the Mango app`);
    return fallback;
  }
  return tauriInvoke<R>(command, args);
}

const emptySnapshot: T.MatchSnapshot = {
  receivedAtUtc: "",
  isConnected: false,
  gameState: "",
  clockTimeSeconds: 0,
  matchId: "",
  serverSteamId: "",
  playerName: "",
  steamId: "",
  gpm: 0,
  xpm: 0,
  teamName: "",
  heroName: "",
  players: [],
};

const hotkey = (display: string): T.HotkeyView => ({ ctrl: false, alt: true, shift: false, win: false, virtualKey: 0, display });

export const defaults = {
  store: {
    snapshot: emptySnapshot,
    pageSnapshot: emptySnapshot,
    isShowingPreviousMatch: false,
    isGsiConnected: false,
    isUsingMockData: false,
    connectionStatus: "Waiting for Dota 2",
    lastPayloadUtc: null,
    rosterFetchFailed: false,
    rosterFetchMessage: "",
    rosterSource: "",
  } satisfies T.StoreState,
  matchView: { matchId: "", subtitle: "Waiting for Dota 2.", isPreviousMatch: false, isMock: false, players: [] } satisfies T.MatchPageView,
  settings: {
    gsiPort: 40000,
    theme: 0,
    showPlayerPanel: true,
    showBountyTimer: true,
    showPowerTimer: true,
    showWisdomTimer: true,
    showLotusTimer: true,
    showBuildPanel: true,
    overlayVisible: false,
    overlayInteractive: false,
    overlayOpacity: 0.92,
    timerSoundsEnabled: true,
    isTurboMode: false,
    runeTimerLeadSeconds: 15,
    showMockData: false,
    steamApiKey: "",
    stratzApiToken: "",
    toggleOverlayHotkey: hotkey("Alt+F9"),
    toggleInteractiveHotkey: hotkey("Alt+`"),
  } satisfies T.SettingsView,
};

export const api = {
  appInfo: () =>
    invoke<T.AppInfo>("app_info", undefined, {
      version: "dev",
      gsiStatus: "Not running in Mango",
      gsiInstall: { success: false, message: "", paths: [] },
      hotkeyStatus: "",
      usesGameFont: false,
    }),
  reinstallGsiConfig: () => invoke<T.GsiInstallResult>("reinstall_gsi_config"),
  storeState: () => invoke<T.StoreState>("store_state", undefined, defaults.store),
  matchView: () => invoke<T.MatchPageView>("match_view", undefined, defaults.matchView),

  getSettings: () => invoke<T.SettingsView>("get_settings", undefined, defaults.settings),
  updateSettings: (patch: T.SettingsPatch) => invoke<T.SettingsView>("update_settings", { patch }, { ...defaults.settings, ...patch }),

  notesList: () => invoke<T.NoteRow[]>("notes_list", undefined, []),
  noteSave: (playerName: string, playerId: string, content: string) =>
    invoke<void>("note_save", { playerName, playerId, content }),
  noteDelete: (key: string) => invoke<void>("note_delete", { key }),
  noteVote: (playerName: string, playerId: string, vote: "like" | "dislike") =>
    invoke<boolean>("note_vote", { playerName, playerId, vote }),
  noteToggleToxic: (playerName: string, playerId: string) => invoke<boolean>("note_toggle_toxic", { playerName, playerId }),
  notesExport: () => invoke<string | null>("notes_export"),
  notesImportPick: () => invoke<T.ImportPreview | null>("notes_import_pick"),
  notesImportApply: (replace: boolean) => invoke<string>("notes_import_apply", { replace }),

  rankOptions: () => invoke<T.RankOption[]>("rank_options", undefined, []),
  rankSet: (playerId: string, playerName: string, tier: number | null) =>
    invoke<string>("rank_set", { playerId, playerName, tier }),

  playerPicker: () => invoke<T.PickerItem[]>("player_picker", undefined, []),
  localPlayer: () => invoke<string>("local_player", undefined, ""),
  playerIdentity: (steamId: string) =>
    invoke<T.LocalIdentity>("player_identity", { steamId }, { steamId, name: "", note: "", likes: 0, dislikes: 0 }),
  playerProfile: (steamId: string, refresh = false) => invoke<T.PlayerProfile>("player_profile", { steamId, refresh }),

  overlaySetVisible: (visible: boolean) => invoke<void>("overlay_set_visible", { visible }),
  overlaySetInteractive: (interactive: boolean) => invoke<void>("overlay_set_interactive", { interactive }),
  overlayState: () => invoke<T.OverlayView>("overlay_state"),
  overlaySetRegions: (rects: { x: number; y: number; width: number; height: number }[]) =>
    invoke<void>("overlay_set_regions", { rects }),
  overlaySavePosition: (widget: string, x: number, y: number) => invoke<void>("overlay_save_position", { widget, x, y }),
  overlayResetPositions: () => invoke<void>("overlay_reset_positions"),
  hudFont: (weight: "regular" | "semibold") => invoke<ArrayBuffer>("hud_font", { weight }),

  hotkeyDescribe: (key: T.RecordedKey) => invoke<string>("hotkey_describe", { key }),
  hotkeysSave: (toggleOverlay: T.RecordedKey, toggleInteractive: T.RecordedKey) =>
    invoke<string>("hotkeys_save", { toggleOverlay, toggleInteractive }),

  updateCheck: () => invoke<T.UpdateCheck>("update_check"),
  updateInstall: () => invoke<string>("update_install"),
};

/** Opens a link in the default browser. */
export async function openUrl(url: string) {
  if (!url) return;
  if (isTauri) {
    const { openUrl } = await import("@tauri-apps/plugin-opener");
    await openUrl(url);
  } else {
    window.open(url, "_blank", "noopener");
  }
}

export function errorText(error: unknown): string {
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message;
  return "Something went wrong.";
}
