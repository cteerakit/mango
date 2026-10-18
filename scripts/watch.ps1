# Rebuilds and relaunches Mango when source files change.
# Enable About > Open developer options > Show mock data to work without Dota.
param(
    [string]$Project = "$PSScriptRoot\..\src\Mango\Mango.csproj"
)

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$platform = if ($arch -eq 'Arm64') { 'ARM64' } else { 'x64' }

Write-Host 'Watching C# and XAML. Changes rebuild and restart Mango. Press Ctrl+C to stop.'
dotnet watch --no-hot-reload --project $Project run --no-launch-profile --property:Platform=$platform
exit $LASTEXITCODE
