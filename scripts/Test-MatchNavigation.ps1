param([Parameter(Mandatory)][int]$AppPid)

$ErrorActionPreference = 'Stop'

function Get-MatchScroll {
    $result = winapp ui get-property MatchScrollViewer -a $AppPid --json | ConvertFrom-Json
    if ($result.error) { throw $result.error.message }
    return $result
}

function Invoke-Navigation([string]$Name) {
    $tree = winapp ui inspect -a $AppPid --interactive --json | ConvertFrom-Json
    $item = $tree.windows.elements | Where-Object {
        $_.className -eq 'Microsoft.UI.Xaml.Controls.NavigationViewItem' -and
        ($_.name -eq $Name -or $_.automationId -eq $Name)
    } | Select-Object -First 1
    if (-not $item) { throw "Navigation item $Name was not found." }
    winapp ui invoke $item.selector -a $AppPid | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not navigate to $Name." }
}

function Get-PlayerLinks {
    $tree = winapp ui inspect -a $AppPid --interactive --json | ConvertFrom-Json
    return ($tree.windows.elements | Where-Object {
        $_.type -eq 'Hyperlink' -and $_.automationId -ne 'ViewHeroMatchButton'
    } | ForEach-Object selector | Sort-Object) -join '|'
}

Invoke-Navigation Match
winapp ui wait-for MatchScrollViewer -a $AppPid -t 3000 | Out-Null
winapp ui scroll MatchScrollViewer -a $AppPid --direction down | Out-Null
$before = Get-MatchScroll
$playerLinks = Get-PlayerLinks
if (-not $playerLinks) { throw 'No roster links found.' }
if ([double]$before.properties.ScrollVerticalPercent -le 0) {
    throw 'Use a preview roster with enough rows to scroll.'
}

foreach ($destination in @('Settings', 'NavYou')) {
    Invoke-Navigation $destination
    Invoke-Navigation Match
    winapp ui wait-for MatchScrollViewer -a $AppPid -t 3000 | Out-Null
    $after = Get-MatchScroll
    if ($after.elementId -ne $before.elementId) {
        throw "Match page was recreated after visiting $destination."
    }
    if ((Get-PlayerLinks) -ne $playerLinks) {
        throw "Unchanged player rows were rebuilt after visiting $destination."
    }
    if ([Math]::Abs([double]$after.properties.ScrollVerticalPercent - [double]$before.properties.ScrollVerticalPercent) -gt 0.1) {
        throw "Match page lost its scroll position after visiting $destination."
    }
    Write-Host "PASS: Match page and scroll position retained after $destination."
}
