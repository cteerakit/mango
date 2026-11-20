import { useEffect, useState } from "react";
import { Button, Card, Notice, Page, cx } from "../components/ui";
import { api, errorText } from "../lib/api";
import { useSettings } from "../lib/hooks";
import type { HotkeyView, RecordedKey } from "../lib/types";

const MODIFIER_CODES = new Set(["ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight"]);

interface Editing {
  key: RecordedKey;
  display: string;
}

const fromView = (v: HotkeyView): Editing => ({
  key: { ctrl: v.ctrl, alt: v.alt, shift: v.shift, win: v.win, virtualKey: v.virtualKey },
  display: v.display,
});

const DEFAULTS = {
  overlay: { key: { ctrl: false, alt: true, shift: false, win: false, virtualKey: 0x78 }, display: "Alt+F9" },
  interactive: { key: { ctrl: false, alt: true, shift: false, win: false, virtualKey: 0xc0 }, display: "Alt+`" },
};

export function ShortcutsPage() {
  const [settings] = useSettings();
  const [overlay, setOverlay] = useState<Editing>(fromView(settings.toggleOverlayHotkey));
  const [interactive, setInteractive] = useState<Editing>(fromView(settings.toggleInteractiveHotkey));
  const [recording, setRecording] = useState<"overlay" | "interactive" | null>(null);
  const [message, setMessage] = useState<{ tone: "success" | "critical" | "neutral"; text: string } | null>(null);

  useEffect(() => {
    setOverlay(fromView(settings.toggleOverlayHotkey));
    setInteractive(fromView(settings.toggleInteractiveHotkey));
  }, [settings.toggleOverlayHotkey, settings.toggleInteractiveHotkey]);

  useEffect(() => {
    if (!recording) return;
    const onKey = async (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.code === "Escape" && !e.altKey && !e.ctrlKey && !e.shiftKey) return setRecording(null);
      if (MODIFIER_CODES.has(e.code)) return;
      const key: RecordedKey = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, code: e.code };
      try {
        const display = await api.hotkeyDescribe(key);
        (recording === "overlay" ? setOverlay : setInteractive)({ key, display });
        setMessage(null);
      } catch (error) {
        setMessage({ tone: "critical", text: errorText(error) });
      }
      setRecording(null);
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [recording]);

  const save = async () => {
    try {
      const text = await api.hotkeysSave(overlay.key, interactive.key);
      setMessage({ tone: text.startsWith("Saved, but") ? "critical" : "success", text });
    } catch (error) {
      setMessage({ tone: "critical", text: errorText(error) });
    }
  };

  return (
    <Page title="Shortcuts" subtitle="Click Change, then press the new key combination. Include at least one modifier.">
      {message && (
        <Notice tone={message.tone} onClose={() => setMessage(null)}>
          {message.text}
        </Notice>
      )}
      <Card className="divide-y divide-line">
        <ShortcutRow label="Toggle overlay" value={overlay.display} recording={recording === "overlay"} onChange={() => setRecording("overlay")} />
        <ShortcutRow
          label="Interactive mode"
          description="Drag overlay widgets with the mouse; press again to lock them."
          value={interactive.display}
          recording={recording === "interactive"}
          onChange={() => setRecording("interactive")}
        />
      </Card>
      <div className="flex gap-2">
        <Button variant="primary" onClick={save}>
          Save shortcuts
        </Button>
        <Button
          onClick={() => {
            setOverlay(DEFAULTS.overlay);
            setInteractive(DEFAULTS.interactive);
            setMessage({ tone: "neutral", text: "Defaults restored — click Save shortcuts to apply." });
          }}
        >
          Reset defaults
        </Button>
      </div>
    </Page>
  );
}

function ShortcutRow({ label, description, value, recording, onChange }: {
  label: string;
  description?: string;
  value: string;
  recording: boolean;
  onChange: () => void;
}) {
  return (
    <div className="flex items-center justify-between gap-4 p-5">
      <div>
        <div className="text-sm font-medium">{label}</div>
        {description && <div className="mt-0.5 text-sm text-muted">{description}</div>}
      </div>
      <div className="flex items-center gap-3">
        <kbd
          className={cx(
            "min-w-28 rounded-md border px-3 py-1.5 text-center font-mono text-sm",
            recording ? "border-accent text-accent" : "border-line bg-surface-2",
          )}
        >
          {recording ? "Press keys…" : value}
        </kbd>
        <Button onClick={onChange} disabled={recording}>
          Change
        </Button>
      </div>
    </div>
  );
}
