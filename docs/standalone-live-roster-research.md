# Standalone live roster investigation

Investigated 2026-10-18. Scope: Windows, an already running Dota client, no Overwolf, no game-memory access or injection, no user credentials, and no interruption of the active game.

## Finding

Local Steam Friends source enumeration was verified against playing match `9050691699`: server `90295163310425094` returned eight accounts, including the local player. The packaged PowerShell helper repeated that result with player names, including UTF-8 names. This is a partial roster source; ten-player coverage, teams, heroes, and live combat statistics are not supplied by this interface. [Valve ISteamFriends reference](https://partner.steamgames.com/doc/api/ISteamFriends#GetFriendCountFromSource)

Mango now reads the match-to-server mapping from the existing Dota console log and runs this query in an isolated helper process using the game's installed `steam_api64.dll`. The helper initializes its own Steam API context and only calls Steam Friends read methods. It does not consume Game Coordinator messages. Results must match the requested server, include the local account, and contain at most ten members. Missing teams/heroes remain unknown, and existing web sources remain available.

The probe required `SteamAPI_InitFlat` and the installed DLL's `SteamAPI_SteamFriends_v018` accessor. Uninitialized interface probes failed and were not integrated. A development `steam_appid.txt` containing 570 is created only in the helper's temporary working directory and removed afterward; it is not packaged next to Mango. This empirical success does not establish Valve support for distributing a companion that initializes under Dota's app ID. That remains a distribution/support question, distinct from whether the local data is available.

Final running-build verification: match `9050691699` stayed in `DOTA_GAMERULES_STATE_GAME_IN_PROGRESS` with one normalized GSI player. Mango resolved server `90295163310425094`, then recorded a ten-player `steam-realtime` resolution. Windows UI Automation confirmed ten player-name links on the Match page, and its screenshot showed team groups and hero icons. The realtime parser now rejects a conflicting returned match ID; top-live/OpenDota account fallback also rejects conflicting known match IDs. All 49 tests passed and the app built and launched successfully. This verifies this live match, not universal coverage of every matchmaking server.

Do not treat historical coplay users as the current roster or assume a second Game Coordinator consumer is isolated from Dota.

## Existing local evidence

These observations were supplied by the parent investigation, not independently replayed by this research task:

- GSI identifies the local player during playing mode; spectator mode supplies other players.
- Current match `9050650286` / local account `1088417288` is absent from the checked Steam top-live partitions and OpenDota live list.
- The existing console log maps the match to server `90295161133586447`.
- `IDOTA2MatchStats_570/GetRealtimeStats/v1` returns HTTP 400 for that server. A known public server returns HTTP 200 with root-level `teams`; Mango's response parser has been corrected separately.

The HTTP 400 does not establish that the match can never become available or that every API requires the same visibility. It does rule out claiming the current direct request works.

## Local Steam interfaces

### Source membership: a documented candidate

`ISteamFriends.GetFriendCountFromSource(serverSteamId)` and `GetFriendFromSourceByIndex(serverSteamId, index)` enumerate accounts in a source, including a game server. A zero count can mean unavailable local data. This is an API capability, not proof Dota fills that cache. [Valve ISteamFriends reference](https://partner.steamgames.com/doc/api/ISteamFriends#GetFriendCountFromSource)

`ISteamClient` offers `CreateSteamPipe`, `ConnectToGlobalUser`, and `GetISteamFriends`; the global user is the existing Steam user. This provides a documented connection path without requesting a password. It does not promise Dota-specific cache visibility across callers. [Valve ISteamClient reference](https://partner.steamgames.com/doc/api/ISteamClient)

Suggested bounded proof:

1. Connect a separate helper to the existing Steam client through documented interfaces, owning its own pipe.
2. Read the source count for the exact server recorded for the current match; if nonzero, enumerate and record the account IDs.
3. Require the local account and an observed complete player set before accepting this as a match roster. Confirm identifiers against visible in-game players.
4. Confirm the cache changes on a subsequent match; reject results belonging to an earlier server or match.
5. Do not write Steam state, call `SetPlayedWith`, send GC hello/watch/party commands, or consume GC messages as part of this proof.

The output contains identities, not team/slot/hero mapping. Even a successful enumeration needs a trustworthy mapping source or a UI that honestly represents unknown teams.

Steam API initialization has an application-ID and same-OS-user context requirement. Valve describes `steam_appid.txt` as a development mechanism and says not to ship it. A successful diagnostic initialized as Dota's app ID would therefore still leave production packaging/support questions. [Valve API initialization documentation](https://partner.steamgames.com/doc/sdk/api#initialization_and_shutdown)

The current mirrored Valve SDK uses `SteamClient023`; its `SteamClient()` accessor calls `SteamInternal_CreateInterface`. For dynamic DLL consumers, Valve's header recommends `SteamAPI_InitFlat`, which returns an initialization result and can populate an error buffer. These facts do not establish a supported pre-initialization factory bypass or guarantee that registering another consumer as app 570 has no effects on the running game. [Valve-authored client header mirror](https://github.com/rlabrecque/SteamworksSDK/blob/master/public/steam/isteamclient.h), [initialization header mirror](https://github.com/rlabrecque/SteamworksSDK/blob/master/public/steam/steam_api.h)

### Friends and coplay: insufficient to prove current membership

`GetFriendGamePlayed` reports a known friend's game and server/lobby metadata; it does not enumerate strangers. Coplay enumeration returns users recorded through `SetPlayedWith`, with game/time history rather than a current match key. These are discovery hints, not evidence that ten accounts share this match. [Valve ISteamFriends reference](https://partner.steamgames.com/doc/api/ISteamFriends)

### Game Coordinator: protocol exists, safe concurrent access unproved

The official `ISteamGameCoordinator` interface sends messages and retrieves queued messages; `RetrieveMessage` removes the first message. Its documentation does not guarantee that another process or pipe has a separately addressed Dota message queue. Connecting through local Steam IPC alone therefore does not prove that polling GC messages will leave the active Dota client unaffected. [Valve ISteamGameCoordinator reference](https://partner.steamgames.com/doc/api/ISteamGameCoordinator)

The extracted current Dota protocol has useful structures:

- `CMsgClientToGCFindTopSourceTVGames`: search key, pagination, and explicit `lobby_ids`.
- Its response includes games with account/hero/team fields, server ID, lobby ID, and match ID.
- `CMsgClientToGCMatchesMinimalRequest` accepts match IDs, but the schema does not establish that an unfinished match is queryable.
- `CMsgSpectateFriendGame` returns server identity, with explicit eligibility failures. It is not a general roster request.

These are definitions extracted from Valve's client, mirrored by SteamTracking; they are primary protocol artifacts, not an official stability guarantee. No safe runtime proof was attempted in this research task. [Current Dota watch protocol definitions](https://github.com/SteamTracking/Protobufs/blob/master/dota2/dota_gcmessages_client_watch.proto)

Do not ship a GC client on the strength of these definitions. First establish queue ownership and response routing outside the user's active match, then demonstrate a supported query returns the missing roster. A request named “top games” may have the same discoverability limitation as the existing web list.

## Logs and public HTTP

Reading an existing console log is useful because this session already obtained the server ID that way. No primary source found here establishes a current always-on local log containing all ten account IDs during playing mode.

Valve's February 2023 update explicitly disabled matchmaking introspection commands including `dota_lobby_debug`, as well as live `record`; the old lobby-dump recipe is not a current solution. [Valve update announcement](https://www.dota2.com/newsentry/3659774959178253450?l=english) (The announcement is client-rendered; search-indexed localized copies expose the relevant text.)

No documented direct public HTTP endpoint was found that guarantees an unfinished ordinary match roster from only a match ID or player ID. The keyless `ISteamWebAPIUtil/GetSupportedAPIList/v1` response queried during this research omitted Dota interfaces, so that enumeration is not evidence that no other Dota endpoints exist. Existing match-history/profile endpoints must not be presented as live-roster guarantees.

## Decision

Keep Mango standalone. Use the verified local server-source enumeration as a partial roster fallback alongside the web sources. If the local cache is empty, do not substitute old coplay data. Do not claim complete ten-player coverage or known team/hero details from this source. Console logging must already expose the exact current match-to-server mapping; Mango does not restart Dota or enable console commands during a match.
