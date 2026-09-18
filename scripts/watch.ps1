# Rebuilds and relaunches Mango when source files change.
# Packaged WinUI apps cannot receive in-process Hot Reload from `dotnet watch`
# (WinApp CLI launches via AUMID). Use Visual Studio F5 for XAML / C# Hot Reload.
param(
    [string]$Project = "$PSScriptRoot\..\src\Mango\Mango.csproj"
)

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$platform = if ($arch -eq 'Arm64') { 'ARM64' } else { 'x64' }

dotnet watch --non-interactive --project $Project -- -p:Platform=$platform
