// Shapes of what the Rust side sends (serde camelCase). Keep in sync with src-tauri.

export interface MatchBuildItem {
  name: string;
  imageUrl: string;
  slot: string;
  purchaseTimeSeconds: number | null;
}

export interface RecentMatchSummary {
  heroName: string;
  heroImageUrl: string;
  result: string;
}

export interface PlayerMatchHistory {
  kda: string;
  result: string;
  position: number | null;
  positionBasis: string;
  isPositionEstimated: boolean;
  playedAt: string | null;
  encounterLabel: string;
  heroMatchLabel: string;
  heroMatchUrl: string;
  items: MatchBuildItem[];
  recentMatches: RecentMatchSummary[];
  recentMatchesLabel: string;
}

export interface MatchPlayer {
  steamId: string;
  name: string;
  teamName: string;
  teamSlot: number;
  heroName: string;
  heroLevel: number;
  kills: number;
  deaths: number;
  assists: number;
  lastHits: number;
  denies: number;
  gpm: number;
  xpm: number;
  gold: number;
  note: string;
  likeCount: number;
  dislikeCount: number;
  isToxic: boolean;
  canVote: boolean;
  voteUnavailableReason: string;
  currentMatchVote: string;
  avatarUrl: string;
  heroImageUrl: string;
  rankIconUrl: string;
  rankStarUrl: string;
  rankLabel: string;
  rankIsEstimated: boolean;
  reportedRankTier: number | null;
  matchesLabel: string;
  isProfilePrivate: boolean;
  isLocalPlayer: boolean;
  history: PlayerMatchHistory;
}

export interface PlayerRow extends MatchPlayer {
  heroKey: string;
  displayHeroName: string;
  canEditRank: boolean;
  canMarkToxic: boolean;
  positionLabel: string;
  resultKda: string;
}

export interface MatchPageView {
  matchId: string;
  subtitle: string;
  isPreviousMatch: boolean;
  isMock: boolean;
  players: PlayerRow[];
}

export interface MatchSnapshot {
  receivedAtUtc: string;
  isConnected: boolean;
  gameState: string;
  clockTimeSeconds: number;
  matchId: string;
  serverSteamId: string;
  playerName: string;
  steamId: string;
  gpm: number;
  xpm: number;
  teamName: string;
  heroName: string;
  players: MatchPlayer[];
}

export interface StoreState {
  snapshot: MatchSnapshot;
  pageSnapshot: MatchSnapshot;
  isShowingPreviousMatch: boolean;
  isGsiConnected: boolean;
  isUsingMockData: boolean;
  connectionStatus: string;
  lastPayloadUtc: string | null;
  rosterFetchFailed: boolean;
  rosterFetchMessage: string;
  rosterSource: string;
}

export interface HotkeyView {
  ctrl: boolean;
  alt: boolean;
  shift: boolean;
  win: boolean;
  virtualKey: number;
  display: string;
}

export interface SettingsView {
  gsiPort: number;
  /** 0 System, 1 Light, 2 Dark */
  theme: number;
  showPlayerPanel: boolean;
  showBountyTimer: boolean;
  showPowerTimer: boolean;
  showWisdomTimer: boolean;
  showLotusTimer: boolean;
  showBuildPanel: boolean;
  overlayVisible: boolean;
  overlayInteractive: boolean;
  overlayOpacity: number;
  timerSoundsEnabled: boolean;
  isTurboMode: boolean;
  runeTimerLeadSeconds: number;
  showMockData: boolean;
  steamApiKey: string;
  stratzApiToken: string;
  toggleOverlayHotkey: HotkeyView;
  toggleInteractiveHotkey: HotkeyView;
}

export type SettingsPatch = Partial<
  Pick<
    SettingsView,
    | "theme"
    | "showPlayerPanel"
    | "showBountyTimer"
    | "showPowerTimer"
    | "showWisdomTimer"
    | "showLotusTimer"
    | "showBuildPanel"
    | "overlayOpacity"
    | "timerSoundsEnabled"
    | "isTurboMode"
    | "runeTimerLeadSeconds"
    | "showMockData"
    | "steamApiKey"
    | "stratzApiToken"
  >
>;

export interface GsiInstallResult {
  success: boolean;
  message: string;
  paths: string[];
}

export interface AppInfo {
  version: string;
  gsiStatus: string;
  gsiInstall: GsiInstallResult;
  hotkeyStatus: string;
  usesGameFont: boolean;
}

export interface NoteRow {
  key: string;
  playerName: string;
  playerId: string;
  content: string;
  likeCount: number;
  dislikeCount: number;
  isToxic: boolean;
  updatedAt: string | null;
}

export interface RankOption {
  tier: number;
  label: string;
}

export interface PickerItem {
  steamId: string;
  name: string;
}

export interface LocalIdentity {
  steamId: string;
  name: string;
  note: string;
  likes: number;
  dislikes: number;
}

export interface PlayerMatchRow {
  matchId: string;
  heroName: string;
  result: string;
  kda: string;
  duration: string;
  playedAt: string;
}

export interface PlayerOverview {
  avatarUrl: string;
  steamId: string;
  displayName: string;
  rankTier: number | null;
  displayRankTier: number | null;
  rankIsEstimated: boolean;
  rankLabel: string;
  wins: number;
  losses: number;
  winRatePercent: number | null;
  isPrivate: boolean;
  recentMatches: PlayerMatchRow[];
}

export interface PlayerProfile {
  overview: PlayerOverview | null;
  rankText: string;
  error: string | null;
}

export interface UpdateCheck {
  status: string;
  downloadUrl: string | null;
  checksumUrl: string | null;
}

export interface ImportPreview {
  imported: number;
  existing: number;
}

export interface TimerView {
  id: "bounty" | "power" | "wisdom" | "lotus";
  name: string;
  countdown: string;
  secondsUntil: number | null;
  visible: boolean;
}

export type Point = [number, number] | null;

export interface OverlayView {
  gpm: number;
  xpm: number;
  interactive: boolean;
  opacity: number;
  showPlayerPanel: boolean;
  showBuildPanel: boolean;
  layout: { player: Point; bounty: Point; power: Point; wisdom: Point; lotus: Point; build: Point };
  timers: TimerView[];
  buildItems: string[];
  alert: string | null;
  toggleInteractiveHint: string;
}

export interface RecordedKey {
  ctrl: boolean;
  alt: boolean;
  shift: boolean;
  win: boolean;
  code?: string;
  virtualKey?: number;
}
