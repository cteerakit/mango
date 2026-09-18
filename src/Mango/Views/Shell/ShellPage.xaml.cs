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
        UpdateBriefStatusChrome();
        if (NavView.SelectedItem is null && NavView.MenuItems.Count > 0)
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

    private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        UpdateBriefStatusChrome();

    private void NavView_PaneOpened(NavigationView sender, object args) => UpdateBriefStatusChrome();

    private void NavView_PaneClosed(NavigationView sender, object args) => UpdateBriefStatusChrome();

    private void UpdateBriefStatusChrome()
    {
        var showLabel = NavView.IsPaneOpen || NavView.DisplayMode == NavigationViewDisplayMode.Expanded;
        BriefStatusLabel.Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed;
        BriefStatusPanel.HorizontalAlignment = showLabel ? HorizontalAlignment.Left : HorizontalAlignment.Center;
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
            "player" => typeof(PlayerPage),
            "overlay" => typeof(OverlayPage),
            "settings" => typeof(SettingsPage),
            "shortcuts" => typeof(ShortcutsPage),
            "about" => typeof(AboutPage),
            _ => typeof(StatusPage),
        };

        ContentFrame.Navigate(pageType, ViewModel);
        ContentFrame.BackStack.Clear();
        NavView.IsBackEnabled = false;
    }
}
