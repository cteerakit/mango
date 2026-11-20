import { useCallback, useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { api, defaults, isTauri } from "./api";
import type { MatchPageView, SettingsView, StoreState } from "./types";

/** Subscribes to a Rust event for the component's lifetime. */
export function useTauriEvent<P>(event: string, handler: (payload: P) => void) {
  useEffect(() => {
    if (!isTauri) return;
    let disposed = false;
    let unlisten: (() => void) | undefined;
    listen<P>(event, (e) => handler(e.payload)).then((fn) => {
      if (disposed) fn();
      else unlisten = fn;
    });
    return () => {
      disposed = true;
      unlisten?.();
    };
    // The handler is captured once per event name; callers pass stable setters.
  }, [event]);
}

/** Loads with `load`, then keeps the value current from `event`. */
function useLive<T>(event: string, initial: T, load: () => Promise<T>): T {
  const [value, setValue] = useState(initial);
  useEffect(() => {
    load().then(setValue).catch(() => {});
  }, [load]);
  useTauriEvent<T>(event, setValue);
  return value;
}

export const useStoreState = (): StoreState => useLive("store-state", defaults.store, api.storeState);
export const useMatchView = (): MatchPageView => useLive("match-view", defaults.matchView, api.matchView);

export function useSettings(): [SettingsView, (patch: Parameters<typeof api.updateSettings>[0]) => Promise<void>] {
  const settings = useLive("settings", defaults.settings, api.getSettings);
  const [local, setLocal] = useState(settings);
  useEffect(() => setLocal(settings), [settings]);
  const update = useCallback(async (patch: Parameters<typeof api.updateSettings>[0]) => {
    setLocal((current) => ({ ...current, ...patch }));
    const next = await api.updateSettings(patch);
    setLocal(next);
  }, []);
  return [local, update];
}

/** Bumps whenever notes or ranks change, so pages can reload them. */
export function useNotesVersion(): number {
  const [version, setVersion] = useState(0);
  useTauriEvent("notes-changed", () => setVersion((v) => v + 1));
  return version;
}

/** Re-renders every `ms` (for relative times). */
export function useNow(ms = 30_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(id);
  }, [ms]);
  return now;
}
