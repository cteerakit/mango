# Party identification: protocol evidence

Investigated 2026-10-18. This note combines source inspection with a live Steam Friends probe. It verifies local-party membership in the observed spectating state; it does not establish full-match party coverage. No GC session, message retrieval, party command, game-memory access, or game interaction was performed for this investigation.

## What the protocol actually contains

The extracted lobby schema defines `CSODOTAStaticLobbyMember.party_id` (field 2) and `CSODOTAStaticLobby.all_members` (field 1). The ordinary `CSODOTALobbyMember` has a player `id`, but no party field; `CSODOTALobby` contains those members and separate member-index arrays. Static members have names but no Steam ID. Consequently, reading the ordinary lobby object alone is insufficient. Joining its members to a separate static object requires a verified index association; name matching would be unreliable. The schema distinguishes static data from `CSODOTAServerStaticLobby`, but that naming does not establish which objects a companion receives. [Extracted lobby schema, pinned revision](https://github.com/SteamTracking/Protobufs/blob/70f23ca6c420b4b32b2c742ad7ee178ed16617de/dota2/dota_gcmessages_common_lobby.proto#L119)

A read-only scan of every `.proto` file in the current repository's `dota2` directory found `CSODOTAStaticLobby` only in its declarations. This does not identify a GC request, shared-object type mapping, subscription, or delivery guarantee. A protobuf definition proves the shape exists; it does not prove an independent process can obtain it. The inspected lobby file's latest path revision was `70f23ca6c420b4b32b2c742ad7ee178ed16617de`, dated 2026-10-07. Web-indexed raw content lagged the direct GitHub response, so the pinned link is preferable.

Separately, `CSODOTAParty` has `party_id`, `leader_id`, repeated `member_ids`, and UI, matchmaking, and in-match states. That represents one party object. The definition does not promise that a client receives every other party in its match, nor provide a public lookup for an arbitrary player's party. Obtaining the local party would therefore establish only local membership unless other delivery is observed. [Extracted party schema](https://github.com/SteamTracking/Protobufs/blob/master/dota2/dota_gcmessages_common_match_management.proto)

## Steam rich presence is the least invasive candidate

Valve documents `RequestFriendRichPresence` for nonfriends, including users sharing a lobby or game server; requests are rate limited. Read the keys after the update callback. An empty value or zero key count means information is unavailable, not that the player queued solo. The special `steam_player_group` key is deliberately game-defined: it may identify a party, a server, or another group. `steam_player_group_size` reports the corresponding group size. Equal values therefore cannot be treated as matchmaking parties until Dota's actual behavior is checked. [Valve Steam Friends API](https://partner.steamgames.com/doc/api/ISteamFriends#RequestFriendRichPresence)

Useful runtime proof requires a known party of at least two people and known outsiders on the same server. Capture requested rich-presence keys and compare group IDs across both groups. Repeat before matchmaking, during a match, and after party membership changes; test nonfriends as well as friends. Require fresh data and current match/server association before attaching any result to the Match page. Even a successful local-party observation would leave unknown opponents unknown.

## Why GC is not a passive fallback

Valve's `ISteamGameCoordinator.RetrieveMessage` reads the first message in a queue; an undersized buffer leaves that message queued. The public interface does not document an independent copy of Dota's queue for a second process or pipe. It consequently supplies no basis for claiming concurrent consumption is harmless. A diagnostic must establish queue ownership and routing separately before reading alongside an active game; GC hello or party commands are unnecessary for the rich-presence test. [Valve Game Coordinator API](https://partner.steamgames.com/doc/api/ISteamGameCoordinator#RetrieveMessage)

## GSI and the remaining limit

Mango's existing GSI model and prior local investigation contain no party identity. That is evidence about this integration, not proof that no future Dota build can add party output. Inspect raw payloads as well as the normalized model when checking the current client, because unknown JSON properties can be discarded by deserialization. [GSI payload model](../src/Mango.Core/Models/Gsi/GsiPayload.cs), [prior live-roster investigation](standalone-live-roster-research.md)

The source evidence supports a bounded rich-presence experiment. It does not yet justify shipping universal party badges or identifying absent data as solo players.

## Runtime verification

A successful local read was performed through `RequestFriendRichPresence` followed by Steam callbacks, without consuming GC messages. The self record's status was `#DOTA_RP_SPECTATING`. Its `party` value contained `party_id: 30067966414513466`, `party_state: UI`, `open: false`, and two member Steam IDs. `steam_player_group` matched that party ID and its size was 2. There was a matching party subscription in the preexisting console log at 21:04:31.

This establishes a concrete Dota-specific source for that local party in the observed client state. It does not yet establish access to unrelated players' parties or live playing-mode coverage. The bounded diagnostic is [Read-SteamPartyPresence.ps1](../scripts/Read-SteamPartyPresence.ps1).

A second probe requested the local account, the explicitly listed second party member, and the three other accounts still cached for the earlier match server. The local record repeated the explicit two-member party. All four other accounts returned zero rich-presence keys during the bounded three-second callback-pumping window. An update callback was not individually tracked, so these empty results do not establish a privacy rule or prove the data can never be obtained. The server was from an earlier match and is not evidence of a current live roster. The second member was independently listed in the local `party` record even though their own presence was empty.

The console log had no newer playing-match connection than the earlier match and the current presence said spectating. The existing Mango GSI metadata was stale, so it was not treated as a fresh payload or proof about current party fields. No raw GSI capture was performed in this run.

The probe successfully ran in Windows PowerShell (`powershell.exe`), with a temporary working directory holding `steam_appid.txt` containing 570 and the installed game's `steam_api64.dll`. The temporary file and directory were removed afterward. Running directly in the host PowerShell 7 process failed Steam initialization with result 1; that process was not used for the successful observations. As in the earlier roster experiment, runtime success under app ID 570 does not establish a supported distribution mechanism for an independent companion.

### Conclusion

A standalone process can read an explicit Dota local-party ID and member list through Steam Friends in the observed state, without a GC consumer. Mango can potentially show confirmed local-party membership from this source. Universal live-match party identification is still unverified. Before integrating badges, validate a current playing match and party changes, associate evidence with that match, and leave unavailable players unknown. The diagnostic only reports evidence; it does not infer party membership or modify Mango's Match page.
