using Mango.Services;
using Mango.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class AboutPage : Page
{
    private ShellViewModel? _viewModel;

    public AboutPage()
    {
        InitializeComponent();
        ShellContentLayout.Attach(LayoutRoot, ContentColumn);
        VersionText.Text = new AppUpdateService().Version;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ShellViewModel viewModel)
        {
            _viewModel = viewModel;
        }
    }

    private void OnDeveloperClick(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(DeveloperPage), _viewModel);
    }

}
