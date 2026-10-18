# Diagnostic only. Use Windows PowerShell (powershell.exe), not the host PowerShell 7.
# Run in a temporary working directory containing steam_appid.txt (570).
# Reads Steam Friends presence; never sends or retrieves Game Coordinator messages.
param(
    [Parameter(Mandatory = $true)][string]$ApiLibrary,
    [Parameter(Mandatory = $true)][string]$ServerSteamId,
    [string[]]$SteamIds = @()
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MangoSteamPartyPresence
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr library, string name);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_InitFlat(IntPtr error);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_Shutdown();
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_ISteamFriends_GetFriendCountFromSource(IntPtr friends, ulong source);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong SteamAPI_ISteamFriends_GetFriendFromSourceByIndex(IntPtr friends, ulong source, int index);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr friends, ulong player);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FriendsAccessor();

    public sealed class Player
    {
        public string steamId { get; set; }
        public string name { get; set; }
        public Dictionary<string, string> presence { get; set; }
    }

    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr friends, ulong player);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_ISteamFriends_GetFriendRichPresenceKeyCount(IntPtr friends, ulong player);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresenceKeyByIndex(IntPtr friends, ulong player, int index);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr friends, ulong player, string key);
    [DllImport("steam_api64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SteamAPI_RunCallbacks();

    public static Player[] Read(string libraryPath, ulong server, ulong[] extraIds)
    {
        IntPtr library = LoadLibrary(libraryPath);
        if (library == IntPtr.Zero) throw new InvalidOperationException("Steam API library unavailable.");
        IntPtr error = Marshal.AllocHGlobal(1024);
        int initialized;
        try { Marshal.WriteByte(error, 0); initialized = SteamAPI_InitFlat(error); }
        finally { Marshal.FreeHGlobal(error); }
        if (initialized != 0) throw new InvalidOperationException("Steam API initialization failed: " + initialized);
        try
        {
            IntPtr accessor = GetProcAddress(library, "SteamAPI_SteamFriends_v018");
            if (accessor == IntPtr.Zero) accessor = GetProcAddress(library, "SteamAPI_SteamFriends_v017");
            if (accessor == IntPtr.Zero) throw new InvalidOperationException("Steam Friends interface unavailable.");
            IntPtr friends = ((FriendsAccessor)Marshal.GetDelegateForFunctionPointer(accessor, typeof(FriendsAccessor)))();
            if (friends == IntPtr.Zero) throw new InvalidOperationException("Steam Friends interface unavailable.");
            int count = SteamAPI_ISteamFriends_GetFriendCountFromSource(friends, server);
            if (count < 0 || count > 10) count = 0;
            var ids = new HashSet<ulong>(extraIds);
            for (int index = 0; index < count; index++)
                ids.Add(SteamAPI_ISteamFriends_GetFriendFromSourceByIndex(friends, server, index));
            if (ids.Count > 20) throw new InvalidOperationException("Probe limited to twenty accounts.");
            foreach (ulong id in ids) SteamAPI_ISteamFriends_RequestFriendRichPresence(friends, id);
            for (int tick = 0; tick < 30; tick++)
            {
                SteamAPI_RunCallbacks();
                System.Threading.Thread.Sleep(100);
            }
            var players = new List<Player>();
            foreach (ulong id in ids)
            {
                IntPtr name = SteamAPI_ISteamFriends_GetFriendPersonaName(friends, id);
                var bytes = new List<byte>();
                if (name != IntPtr.Zero)
                {
                    for (int offset = 0; offset < 4096; offset++)
                    {
                        byte value = Marshal.ReadByte(name, offset);
                        if (value == 0) break;
                        bytes.Add(value);
                    }
                }
                var presence = new Dictionary<string, string>();
                int keys = SteamAPI_ISteamFriends_GetFriendRichPresenceKeyCount(friends, id);
                for (int index = 0; index < Math.Min(keys, 20); index++)
                {
                    string key = Marshal.PtrToStringAnsi(SteamAPI_ISteamFriends_GetFriendRichPresenceKeyByIndex(friends, id, index));
                    if (string.IsNullOrEmpty(key)) continue;
                    bool allowed = key == "steam_player_group" || key == "steam_player_group_size"
                        || key == "status" || key == "steam_display" || key.IndexOf("party", StringComparison.OrdinalIgnoreCase) >= 0;
                    presence[key] = allowed
                        ? Marshal.PtrToStringAnsi(SteamAPI_ISteamFriends_GetFriendRichPresence(friends, id, key))
                        : "<value omitted>";
                }
                players.Add(new Player { steamId = id.ToString(), name = Encoding.UTF8.GetString(bytes.ToArray()), presence = presence });
            }
            return players.ToArray();
        }
        finally { SteamAPI_Shutdown(); }
    }
}
'@

$mangoPlayers = @([MangoSteamPartyPresence]::Read($ApiLibrary, [uint64]::Parse($ServerSteamId), [uint64[]]$SteamIds))
$mangoResult = @{ serverSteamId = $ServerSteamId; players = $mangoPlayers } | ConvertTo-Json -Compress -Depth 6
[Console]::WriteLine('MANGO_STEAM_PARTY_PRESENCE:' + $mangoResult)
