import { type ReactNode, useCallback, useEffect, useState } from "react";
import { ArrowLeft, Info, Keyboard, Layers, MessageSquare, Settings, Swords, User, Users } from "lucide-react";
import { cx } from "./components/ui";
import { useSettings, useStoreState } from "./lib/hooks";
import type { StoreState } from "./lib/types";
import { AboutPage } from "./pages/AboutPage";
import { DeveloperPage } from "./pages/DeveloperPage";
import { MatchPage } from "./pages/MatchPage";
import { NotesPage } from "./pages/NotesPage";
import { OverlayPage } from "./pages/OverlayPage";
import { PlayerPage } from "./pages/PlayerPage";
import { SettingsPage } from "./pages/SettingsPage";
import { ShortcutsPage } from "./pages/ShortcutsPage";
import { StatusPage } from "./pages/StatusPage";

export type Route =
  | { page: "you" }
  | { page: "match" }
  | { page: "player"; steamId?: string }
  | { page: "overlay" }
  | { page: "settings" }
  | { page: "notes" }
  | { page: "shortcuts" }
  | { page: "about" }
  | { page: "developer" }
  | { page: "status" };

export type Navigate = (route: Route) => void;

const NAV: { page: Route["page"]; label: string; icon: ReactNode }[] = [
  { page: "you", label: "You", icon: <User className="size-4.5" /> },
  { page: "match", label: "Match", icon: <Swords className="size-4.5" /> },
  { page: "player", label: "Player", icon: <Users className="size-4.5" /> },
  { page: "overlay", label: "Overlay", icon: <Layers className="size-4.5" /> },
  { page: "settings", label: "Settings", icon: <Settings className="size-4.5" /> },
  { page: "shortcuts", label: "Shortcuts", icon: <Keyboard className="size-4.5" /> },
  { page: "about", label: "About", icon: <Info className="size-4.5" /> },
];

const THEMES = ["system", "light", "dark"] as const;

declare global {
  interface Window {
    ShippedFeedback?: { open(): void };
  }
}

/** Grey waiting, amber connected in menus, green live, blue mock. */
function statusColor(store: StoreState) {
  if (store.isUsingMockData) return "bg-sky-500";
  if (store.isGsiConnected && store.connectionStatus.includes("Live")) return "bg-success";
  if (store.isGsiConnected) return "bg-caution";
  return "bg-faint";
}

export function App() {
  const [route, setRoute] = useState<Route>({ page: "you" });
  const [history, setHistory] = useState<Route[]>([]);
  const store = useStoreState();
  const [settings] = useSettings();

  useEffect(() => {
    const theme = THEMES[settings.theme] ?? "system";
    if (theme === "system") document.documentElement.removeAttribute("data-theme");
    else document.documentElement.dataset.theme = theme;
  }, [settings.theme]);

  const navigate = useCallback<Navigate>((next) => {
    setRoute((current) => {
      setHistory((h) => [...h, current]);
      return next;
    });
  }, []);
  const select = (page: Route["page"]) => {
    setHistory([]);
    setRoute({ page } as Route);
  };
  const back = () => {
    const previous = history.at(-1);
    if (!previous) return;
    setHistory(history.slice(0, -1));
    setRoute(previous);
  };

  const activeNav = route.page === "notes" ? "settings" : route.page === "developer" ? "about" : route.page;

  return (
    <div className="flex h-full">
      <nav className="flex w-56 shrink-0 flex-col border-r border-line bg-surface" aria-label="Main">
        <div className="flex h-14 items-center gap-2 px-4">
          <button
            type="button"
            aria-label="Back"
            disabled={history.length === 0}
            onClick={back}
            className="inline-flex size-8 items-center justify-center rounded-md text-muted hover:bg-surface-2 hover:text-fg disabled:invisible"
          >
            <ArrowLeft className="size-4.5" />
          </button>
          <span className="flex items-center gap-2 font-display text-lg font-semibold">
            <img src="/mango.png" alt="" className="size-6" />
            Mango
          </span>
        </div>
        <ul className="flex-1 space-y-0.5 overflow-y-auto px-2">
          {NAV.map((item) => (
            <li key={item.page}>
              <button
                type="button"
                onClick={() => select(item.page)}
                aria-current={activeNav === item.page ? "page" : undefined}
                className={cx(
                  "flex h-9 w-full items-center gap-3 rounded-md px-3 text-sm transition-colors",
                  activeNav === item.page
                    ? "bg-surface-3 font-medium text-fg"
                    : "text-muted hover:bg-surface-2 hover:text-fg",
                )}
              >
                {item.icon}
                {item.label}
              </button>
            </li>
          ))}
        </ul>
        <button
          type="button"
          aria-haspopup="dialog"
          onClick={() => {
            if (window.ShippedFeedback) window.ShippedFeedback.open();
            else window.alert("Feedback is currently unavailable. Please try again later.");
          }}
          className="mx-2 flex h-9 shrink-0 items-center gap-3 rounded-md px-3 text-left text-sm text-muted transition-colors hover:bg-surface-2 hover:text-fg"
        >
          <MessageSquare className="size-4.5" aria-hidden="true" />
          Feedback
        </button>
        <button
          type="button"
          onClick={() => select("status")}
          title={store.connectionStatus}
          aria-label={`Open status page. ${store.connectionStatus}`}
          className={cx(
            "m-2 flex items-center gap-3 rounded-md px-3 py-2.5 text-left text-sm hover:bg-surface-2",
            route.page === "status" && "bg-surface-3",
          )}
        >
          <span className={cx("size-2.5 shrink-0 rounded-full", statusColor(store))} />
          <span className="truncate text-muted">{store.connectionStatus}</span>
        </button>
      </nav>
      <main className="min-w-0 flex-1">
        <div className="h-full" hidden={route.page !== "you"}>
          <PlayerPage isSelf active={route.page === "you"} />
        </div>
        <div className="h-full" hidden={route.page !== "player"}>
          <PlayerPage active={route.page === "player"} steamId={route.page === "player" ? route.steamId : undefined} />
        </div>
        {renderPage(route, navigate)}
      </main>
    </div>
  );
}

function renderPage(route: Route, navigate: Navigate) {
  switch (route.page) {
    case "you":
    case "player":
      return null;
    case "match":
      return <MatchPage navigate={navigate} />;
    case "overlay":
      return <OverlayPage />;
    case "settings":
      return <SettingsPage navigate={navigate} />;
    case "notes":
      return <NotesPage />;
    case "shortcuts":
      return <ShortcutsPage />;
    case "about":
      return <AboutPage navigate={navigate} />;
    case "developer":
      return <DeveloperPage />;
    case "status":
      return <StatusPage />;
  }
}
