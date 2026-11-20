import { useEffect, useState } from "react";
import { Download, FileDown, FileUp, NotebookText, RefreshCw } from "lucide-react";
import type { Navigate } from "../App";
import { Button, CardSection, Dialog, Notice, Page, Spinner, cx, inputClass } from "../components/ui";
import { api, errorText, openUrl } from "../lib/api";
import { useSettings } from "../lib/hooks";
import type { ImportPreview, UpdateCheck } from "../lib/types";

const THEMES = [
  { value: 1, label: "Light" },
  { value: 2, label: "Dark" },
  { value: 0, label: "System" },
];

export function SettingsPage({ navigate }: { navigate: Navigate }) {
  const [settings, update] = useSettings();
  const [version, setVersion] = useState("");
  const [updateState, setUpdateState] = useState<UpdateCheck | null>(null);
  const [updateBusy, setUpdateBusy] = useState<"" | "check" | "install">("");
  const [notesStatus, setNotesStatus] = useState("");
  const [error, setError] = useState("");
  const [importPreview, setImportPreview] = useState<ImportPreview | null>(null);

  useEffect(() => {
    api.appInfo().then((i) => setVersion(i.version)).catch(() => {});
  }, []);

  const check = async () => {
    setUpdateBusy("check");
    try {
      setUpdateState(await api.updateCheck());
    } catch (e) {
      setUpdateState({ status: errorText(e), downloadUrl: null, checksumUrl: null });
    } finally {
      setUpdateBusy("");
    }
  };
  const install = async () => {
    setUpdateBusy("install");
    setUpdateState((s) => s && { ...s, status: "Downloading and verifying the update…" });
    try {
      const status = await api.updateInstall();
      setUpdateState((s) => s && { ...s, status });
    } catch (e) {
      setUpdateState((s) => s && { ...s, status: errorText(e) });
      setUpdateBusy("");
    }
  };

  const exportNotes = async () => {
    try {
      const status = await api.notesExport();
      if (status) setNotesStatus(status);
    } catch (e) {
      setError(errorText(e));
    }
  };
  const pickImport = async () => {
    try {
      const preview = await api.notesImportPick();
      if (!preview) return;
      if (preview.existing === 0) setNotesStatus(await api.notesImportApply(false));
      else setImportPreview(preview);
    } catch (e) {
      setError(errorText(e));
    }
  };
  const applyImport = async (replace: boolean) => {
    setImportPreview(null);
    try {
      setNotesStatus(await api.notesImportApply(replace));
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <Page title="Settings" subtitle="Appearance, app updates, API keys, and player-note backup.">
      {error && (
        <Notice tone="critical" onClose={() => setError("")}>
          {error}
        </Notice>
      )}

      <CardSection title="Appearance" description="System follows your Windows app theme. Changes are saved automatically.">
        <div role="radiogroup" aria-label="App theme" className="inline-flex rounded-lg border border-line bg-surface-2 p-1">
          {THEMES.map((t) => (
            <button
              key={t.value}
              type="button"
              role="radio"
              aria-checked={settings.theme === t.value}
              onClick={() => update({ theme: t.value })}
              className={cx(
                "h-8 rounded-md px-4 text-sm",
                settings.theme === t.value ? "bg-surface font-medium text-fg shadow-sm" : "text-muted hover:text-fg",
              )}
            >
              {t.label}
            </button>
          ))}
        </div>
      </CardSection>

      <CardSection title="App updates" description={`Version ${version}`}>
        <p className="text-sm text-muted">
          {updateState?.status ??
            "Check GitHub for a newer version. Mango can download and install it for you; your settings and notes are kept."}
        </p>
        <div className="flex flex-wrap gap-2">
          <Button onClick={check} disabled={!!updateBusy}>
            {updateBusy === "check" ? <Spinner /> : <RefreshCw className="size-4" />} Check for updates
          </Button>
          {updateState?.downloadUrl && (
            <>
              <Button variant="primary" onClick={install} disabled={!!updateBusy}>
                {updateBusy === "install" ? <Spinner /> : <Download className="size-4" />} Install update and restart
              </Button>
              <Button variant="ghost" onClick={() => openUrl(updateState.downloadUrl!)}>
                Download installer manually
              </Button>
            </>
          )}
        </div>
      </CardSection>

      <SecretSection
        title="Steam Web API"
        description="Used to load live teams and heroes on the Match page. Get a key from steamcommunity.com/dev/apikey."
        value={settings.steamApiKey}
        emptyStatus="No Steam API key saved. Teams only appear for games in OpenDota's live list."
        savedStatus="Steam API key saved on this PC."
        onSave={(v) => update({ steamApiKey: v })}
      />
      <SecretSection
        title="STRATZ API"
        description="Used for accurate player positions in past matches. Without a token, positions are estimated from farm. Get a token from stratz.com/api."
        value={settings.stratzApiToken}
        emptyStatus="No STRATZ token saved. Positions are estimated."
        savedStatus="STRATZ token saved on this PC."
        onSave={(v) => update({ stratzApiToken: v })}
      />

      <CardSection
        title="Notes"
        description="Export a JSON backup of player notes, or import one from another PC. Live match notes are matched by Steam ID, not player name."
      >
        <div className="flex flex-wrap gap-2">
          <Button onClick={() => navigate({ page: "notes" })}>
            <NotebookText className="size-4" /> View notes
          </Button>
          <Button onClick={pickImport}>
            <FileUp className="size-4" /> Import notes
          </Button>
          <Button onClick={exportNotes}>
            <FileDown className="size-4" /> Export notes
          </Button>
        </div>
        {notesStatus && <p className="text-sm text-success">{notesStatus}</p>}
      </CardSection>

      <Dialog
        open={!!importPreview}
        title="Import player notes"
        onClose={() => setImportPreview(null)}
        footer={
          <>
            <Button onClick={() => setImportPreview(null)}>Cancel</Button>
            <Button onClick={() => applyImport(true)}>Replace</Button>
            <Button variant="primary" onClick={() => applyImport(false)}>
              Merge
            </Button>
          </>
        }
      >
        You already have {importPreview?.existing} note(s). Merge updates matching players and keeps the rest. Replace
        discards current notes, then imports {importPreview?.imported}.
      </Dialog>
    </Page>
  );
}

/** Password-style input saved when it loses focus or Enter is pressed. */
function SecretSection({ title, description, value, emptyStatus, savedStatus, onSave }: {
  title: string;
  description: string;
  value: string;
  emptyStatus: string;
  savedStatus: string;
  onSave: (value: string) => Promise<void>;
}) {
  const [draft, setDraft] = useState(value);
  const [status, setStatus] = useState(value ? savedStatus : emptyStatus);
  useEffect(() => {
    setDraft(value);
    setStatus(value ? savedStatus : emptyStatus);
  }, [value, savedStatus, emptyStatus]);
  const commit = async () => {
    const next = draft.trim();
    if (next === value) return;
    await onSave(next);
    setStatus(next ? savedStatus.replace(" on this PC", "") : `${title.split(" ")[0]} key cleared.`);
  };
  return (
    <CardSection title={title} description={description}>
      <input
        type="password"
        aria-label={`${title} key`}
        autoComplete="off"
        spellCheck={false}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => e.key === "Enter" && commit()}
        className={inputClass}
      />
      <p className="text-sm text-muted">{status}</p>
    </CardSection>
  );
}
