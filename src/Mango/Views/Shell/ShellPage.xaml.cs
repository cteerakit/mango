using Mango.Services;
using Mango.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; } = new();

    public ShellPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ContentFrame.Navigated += ContentFrame_Navigated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (NavView.SelectedItem is not null)
        {
            return;
        }

        if (NavView.MenuItems.Count > 0)
        {
            NavView.SelectedItem = NavView.MenuItems[0];
        }
    }

    private object? _lastPageParameter;

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        _lastPageParameter = e.Parameter;
        NavView.IsBackEnabled = ContentFrame.CanGoBack;
    }

    internal void ReloadCurrentPage()
    {
        var pageType = ContentFrame.CurrentSourcePageType;
        if (pageType is null)
        {
            return;
        }

        ContentFrame.Navigate(pageType, _lastPageParameter ?? ViewModel);
        ContentFrame.BackStack.Clear();
        NavView.IsBackEnabled = ContentFrame.CanGoBack;
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "status" => typeof(StatusPage),
            "match" => typeof(MatchPage),
            "you" => typeof(PlayerPage),
            "player" => typeof(PlayerPage),
            "overlay" => typeof(OverlayPage),
            "settings" => typeof(SettingsPage),
            "shortcuts" => typeof(ShortcutsPage),
            "about" => typeof(AboutPage),
            _ => typeof(StatusPage),
        };

        ContentFrame.Navigate(pageType, tag == "you" ? new PlayerPageNavigationArgs { IsSelf = true } : ViewModel);
        ContentFrame.BackStack.Clear();
        NavView.IsBackEnabled = false;
    }

}
