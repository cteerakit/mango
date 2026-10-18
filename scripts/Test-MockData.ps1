param(
    [Parameter(Mandatory)][int]$AppPid,
    [string]$SettingsPath
)

$ErrorActionPreference = 'Stop'
if (-not $SettingsPath) {
    $SettingsPath = Join-Path $env:LOCALAPPDATA 'Mango\settings.json'
    $appPath = (Get-Process -Id $AppPid).Path
    $package = Get-AppxPackage | Where-Object {
        $_.InstallLocation -and $appPath.StartsWith($_.InstallLocation + '\', [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if ($package) {
        $packagedSettings = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalCache\Local\Mango\settings.json"
        if (Test-Path $packagedSettings) { $SettingsPath = $packagedSettings }
    }
}

function Invoke-Control([string]$Selector) {
    winapp ui invoke $Selector -a $AppPid | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not invoke $Selector." }
}

function Get-Status {
    $result = winapp ui get-property StatusButton -a $AppPid --json | ConvertFrom-Json
    if ($result.error) { throw $result.error.message }
    return $result.properties.HelpText
}

function Open-DeveloperOptions {
    # Developer options shares About's navigation selection.
    Invoke-Control NavYou
    Invoke-Control About
    winapp ui wait-for OpenDeveloperOptionsButton -a $AppPid -t 3000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'About page did not load.' }
    Invoke-Control OpenDeveloperOptionsButton
    winapp ui wait-for MockDataToggle -a $AppPid -t 3000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Mock data option was not found.' }
}

function Set-MockData([bool]$Enabled) {
    $value = winapp ui get-value MockDataToggle -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw 'Could not read mock data option.' }
    $expected = if ($Enabled) { 'On' } else { 'Off' }
    if ($value.Trim() -ne $expected) { Invoke-Control MockDataToggle }
    winapp ui wait-for MockDataToggle -a $AppPid --value $expected -t 2000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Mock data option did not become $expected." }
}

$original = Get-Content $settingsPath -Raw | ConvertFrom-Json
Open-DeveloperOptions
try {
    # Start with live data, regardless of the saved developer preference.
    Set-MockData $false
    if ((Get-Status) -eq 'Mock data') { throw 'Mock data did not turn off.' }

    Set-MockData $true
    Start-Sleep -Seconds 2
    if ((Get-Status) -ne 'Mock data') { throw 'Mock data was replaced by a live update.' }
    $saved = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if (-not $saved.DebugOverlayPreview) { throw 'Mock data preference was not saved.' }
    if ($saved.OverlayVisible -ne $original.OverlayVisible) { throw 'Mock data changed overlay visibility.' }
    if ($saved.SteamId -ne $original.SteamId) { throw 'Mock data changed the saved Steam identity.' }

    Invoke-Control Match
    winapp ui wait-for MatchScrollViewer -a $AppPid -t 3000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Match page did not load.' }
    if ((Get-Status) -ne 'Mock data') { throw 'Mock data was lost during navigation.' }
    Open-DeveloperOptions
    winapp ui wait-for MockDataToggle -a $AppPid --value On -t 2000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Developer option lost its state.' }

    Set-MockData $false
    if ((Get-Status) -eq 'Mock data') { throw 'Could not return to live data.' }
    Write-Host 'PASS: Mock data persists, survives navigation/live updates, and returns to live data without changing overlay visibility or Steam identity.'
}
finally {
    Open-DeveloperOptions
    Set-MockData ([bool]$original.DebugOverlayPreview)
}
