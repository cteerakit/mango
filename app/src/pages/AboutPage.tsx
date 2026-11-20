import { useEffect, useState } from "react";
import { FlaskConical } from "lucide-react";
import type { Navigate } from "../App";
import { Button, Card, CardSection, Page } from "../components/ui";
import { api } from "../lib/api";

export function AboutPage({ navigate }: { navigate: Navigate }) {
  const [version, setVersion] = useState("—");
  useEffect(() => {
    api.appInfo().then((i) => setVersion(i.version)).catch(() => {});
  }, []);
  return (
    <Page title="About" subtitle="Mango — a Dota 2 live companion.">
      <Card className="flex gap-5 p-5">
        <img src="/mango.png" alt="" className="size-14" />
        <div>
          <h2 className="font-display text-lg font-semibold">Mango</h2>
          <p className="mt-1 text-sm text-muted">
            Mango connects to Dota 2 through Game State Integration (GSI) to show the live match roster, enrich players
            with OpenDota and Steam data, keep local notes and votes, and drive an in-game overlay with objective timers.
          </p>
          <p className="mt-3 text-sm">
            <span className="text-muted">Version</span> <span className="tabular">{version}</span>
          </p>
        </div>
      </Card>
      <CardSection title="Developer" description="Experimental overlay features and debug tools.">
        <Button onClick={() => navigate({ page: "developer" })}>
          <FlaskConical className="size-4" /> Open developer options
        </Button>
      </CardSection>
    </Page>
  );
}
