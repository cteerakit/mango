import { CardSection, Page, Switch } from "../components/ui";
import { useSettings } from "../lib/hooks";

export function DeveloperPage() {
  const [settings, update] = useSettings();
  return (
    <Page title="Developer" subtitle="Experimental features and tools for working on Mango.">
      <CardSection title="Experimental" description="Rough features that may be inaccurate or change without notice.">
        <Switch
          label="Build"
          description="Popular item-build suggestions for your current hero."
          checked={settings.showBuildPanel}
          onChange={(v) => update({ showBuildPanel: v })}
        />
        <Switch
          label="Timer alerts"
          description="Plays a sound as a tracked objective is about to spawn."
          checked={settings.timerSoundsEnabled}
          onChange={(v) => update({ timerSoundsEnabled: v })}
        />
      </CardSection>
      <CardSection title="Debug">
        <Switch
          label="Show mock data"
          description="Shows sample match, player, and overlay data without launching Dota. Turn off to return to live data."
          checked={settings.showMockData}
          onChange={(v) => update({ showMockData: v })}
        />
      </CardSection>
    </Page>
  );
}
