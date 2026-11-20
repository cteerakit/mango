import { useState } from "react";
import { Eye, EyeOff, RotateCcw } from "lucide-react";
import { Button, CardSection, Notice, Page, Switch, inputClass } from "../components/ui";
import { api, errorText } from "../lib/api";
import { useSettings } from "../lib/hooks";

export function OverlayPage() {
  const [settings, update] = useSettings();
  const [message, setMessage] = useState<{ tone: "success" | "critical"; text: string } | null>(null);
  const hotkey = settings.toggleOverlayHotkey.display;

  const setVisible = async (visible: boolean) => {
    try {
      await api.overlaySetVisible(visible);
    } catch (error) {
      setMessage({ tone: "critical", text: errorText(error) });
    }
  };

  return (
    <Page
      title="Overlay"
      subtitle="Choose which overlay widgets stay on screen and how the overlay behaves."
      actions={
        settings.overlayVisible ? (
          <Button onClick={() => setVisible(false)}>
            <EyeOff className="size-4" /> Hide overlay
          </Button>
        ) : (
          <Button variant="primary" onClick={() => setVisible(true)}>
            <Eye className="size-4" /> Show overlay
          </Button>
        )
      }
    >
      {message && (
        <Notice tone={message.tone} onClose={() => setMessage(null)}>
          {message.text}
        </Notice>
      )}
      <Notice>
        Press <strong>{hotkey}</strong> in game to show or hide the overlay, and{" "}
        <strong>{settings.toggleInteractiveHotkey.display}</strong> to drag widgets. Dota must run in Borderless Windowed.
      </Notice>

      <CardSection title="Overlay widgets">
        <Switch
          label="GPM / XPM"
          description="Your GPM and XPM in the same style as the in-game HUD strip."
          checked={settings.showPlayerPanel}
          onChange={(v) => update({ showPlayerPanel: v })}
        />
      </CardSection>

      <CardSection title="Timers" description="Runes show a countdown shortly before they spawn.">
        <div>
          <label htmlFor="lead" className="mb-1.5 block text-sm font-medium">
            Rune countdown lead time (seconds)
          </label>
          <input
            id="lead"
            type="number"
            min={5}
            max={120}
            value={settings.runeTimerLeadSeconds}
            onChange={(e) => {
              const value = Number(e.target.value);
              if (Number.isFinite(value)) update({ runeTimerLeadSeconds: Math.min(120, Math.max(5, Math.round(value))) });
            }}
            className={`${inputClass} w-28`}
          />
          <p className="mt-1.5 text-sm text-muted">How many seconds before a rune spawns its countdown widget appears.</p>
        </div>
        <Switch label="Bounty rune" checked={settings.showBountyTimer} onChange={(v) => update({ showBountyTimer: v })} />
        <Switch label="Power rune" checked={settings.showPowerTimer} onChange={(v) => update({ showPowerTimer: v })} />
        <Switch label="Wisdom rune" checked={settings.showWisdomTimer} onChange={(v) => update({ showWisdomTimer: v })} />
        <Switch label="Lotus pool" checked={settings.showLotusTimer} onChange={(v) => update({ showLotusTimer: v })} />
        <Switch
          label="Turbo mode timers"
          description="Lotus pools refill twice as fast in Turbo."
          checked={settings.isTurboMode}
          onChange={(v) => update({ isTurboMode: v })}
        />
      </CardSection>

      <CardSection title="Appearance">
        <div>
          <div className="mb-1.5 flex justify-between text-sm">
            <label htmlFor="opacity" className="font-medium">
              Overlay opacity
            </label>
            <span className="tabular text-muted">{Math.round(settings.overlayOpacity * 100)}%</span>
          </div>
          <input
            id="opacity"
            type="range"
            min={0.4}
            max={1}
            step={0.05}
            value={settings.overlayOpacity}
            onChange={(e) => update({ overlayOpacity: Number(e.target.value) })}
            className="w-full accent-[var(--accent)]"
          />
        </div>
      </CardSection>

      <CardSection
        title="Widget positions"
        description="Moves every widget back to its default spot: rune countdowns along the right edge, GPM/XPM on the left."
      >
        <Button
          onClick={async () => {
            await api.overlayResetPositions();
            setMessage({ tone: "success", text: "Widget positions restored to defaults." });
          }}
        >
          <RotateCcw className="size-4" /> Reset widget positions
        </Button>
      </CardSection>
    </Page>
  );
}
