import { useCallback, useEffect, useState } from "react";
import { CheckCircle2, Circle, Wrench } from "lucide-react";
import { Button, Card, CardSection, Notice, Page } from "../components/ui";
import { api, errorText } from "../lib/api";
import { useSettings, useStoreState } from "../lib/hooks";
import type { AppInfo } from "../lib/types";

export function StatusPage() {
  const store = useStoreState();
  const [settings] = useSettings();
  const [info, setInfo] = useState<AppInfo | null>(null);
  const [error, setError] = useState("");

  const reload = useCallback(() => api.appInfo().then(setInfo).catch(() => {}), []);
  useEffect(() => {
    reload();
  }, [reload, settings]);

  const reinstall = async () => {
    try {
      await api.reinstallGsiConfig();
      reload();
    } catch (e) {
      setError(errorText(e));
    }
  };

  const lastUpdate = store.lastPayloadUtc ? new Date(store.lastPayloadUtc).toLocaleTimeString() : "—";
  const installed = info?.gsiInstall.success ?? false;
  const overlayKey = settings.toggleOverlayHotkey.display;

  return (
    <Page title="Status" subtitle="Live connection health for GSI and overlay updates, plus the Dota 2 setup checklist.">
      {error && (
        <Notice tone="critical" onClose={() => setError("")}>
          {error}
        </Notice>
      )}
      <Card className="p-5">
        <h2 className="font-display text-base font-semibold">Live status</h2>
        <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
          <dt className="text-muted">GSI listener</dt>
          <dd>{info?.gsiStatus ?? "…"}</dd>
          <dt className="text-muted">Dota 2</dt>
          <dd>{store.connectionStatus}</dd>
          <dt className="text-muted">Last update</dt>
          <dd className="tabular">{lastUpdate}</dd>
          {store.rosterSource && (
            <>
              <dt className="text-muted">Lobby source</dt>
              <dd>{store.rosterSource}</dd>
            </>
          )}
        </dl>
        <p className="mt-4 text-sm text-muted">
          Hotkeys: {overlayKey} toggles the overlay, {settings.toggleInteractiveHotkey.display} interactive mode. If the
          overlay blocks clicks, press {overlayKey} to hide it.
        </p>
        {info?.hotkeyStatus && (
          <div className="mt-3">
            <Notice tone="caution">{info.hotkeyStatus}</Notice>
          </div>
        )}
      </Card>

      <CardSection title="Setup" description="Get Mango connected to Dota 2 Game State Integration.">
        <ol className="space-y-2 text-sm">
          <Step done={installed}>Install GSI config into Dota cfg/gamestate_integration</Step>
          <Step>Steam launch option: -gamestateintegration</Step>
          <Step>Dota video setting: Borderless Windowed</Step>
          <Step done={store.isGsiConnected}>Start a match — overlay updates automatically</Step>
        </ol>
        {info?.gsiInstall.message && (
          <p className={installed ? "text-sm text-muted" : "text-sm text-critical"}>{info.gsiInstall.message}</p>
        )}
        <Button onClick={reinstall}>
          <Wrench className="size-4" /> Reinstall GSI config
        </Button>
      </CardSection>
    </Page>
  );
}

function Step({ done, children }: { done?: boolean; children: string }) {
  return (
    <li className="flex items-center gap-2.5">
      {done ? <CheckCircle2 className="size-4 text-success" /> : <Circle className="size-4 text-faint" />}
      {children}
    </li>
  );
}
