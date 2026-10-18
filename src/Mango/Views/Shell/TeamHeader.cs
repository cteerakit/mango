using Microsoft.UI.Xaml.Media;

namespace Mango.Views.Shell;

public sealed class TeamHeader
{
    public string Title { get; init; } = string.Empty;
    public Brush? AccentBrush { get; init; }
}
