param(
    [Parameter(Mandatory = $true)][string]$ApiLibrary,
    [Parameter(Mandatory = $true)][string]$ServerSteamId
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MangoSteamRoster
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
    }

    public static Player[] Read(string libraryPath, ulong server)
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
            if (count < 1 || count > 10) return new Player[0];
            var players = new List<Player>();
            for (int index = 0; index < count; index++)
            {
                ulong id = SteamAPI_ISteamFriends_GetFriendFromSourceByIndex(friends, server, index);
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
                players.Add(new Player { steamId = id.ToString(), name = Encoding.UTF8.GetString(bytes.ToArray()) });
            }
            return players.ToArray();
        }
        finally { SteamAPI_Shutdown(); }
    }
}
'@

$mangoPlayers = @([MangoSteamRoster]::Read($ApiLibrary, [uint64]::Parse($ServerSteamId)))
$mangoResult = @{ serverSteamId = $ServerSteamId; players = $mangoPlayers } | ConvertTo-Json -Compress -Depth 4
[Console]::WriteLine('MANGO_STEAM_ROSTER:' + $mangoResult)
