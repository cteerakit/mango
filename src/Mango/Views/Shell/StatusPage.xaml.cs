using Mango.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class StatusPage : Page
{
    private ShellViewModel? _viewModel;

    public StatusPage()
    {
        InitializeComponent();
        ShellContentLayout.Attach(LayoutRoot, ContentColumn);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ShellViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        BindFromViewModel();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnNavigatedFrom(e);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.GsiStatus)
            or nameof(ShellViewModel.ConnectionStatus)
            or nameof(ShellViewModel.LastUpdate)
            or nameof(ShellViewModel.HotkeyHintText)
            or nameof(ShellViewModel.InstallMessage)
            or null)
        {
            BindFromViewModel();
        }
    }

    private void BindFromViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        GsiStatusText.Text = _viewModel.GsiStatus;
        ConnectionStatusText.Text = _viewModel.ConnectionStatus;
        LastUpdateText.Text = $"Last update: {_viewModel.LastUpdate}";
        HotkeyHintText.Text = _viewModel.HotkeyHintText;
        InstallMessageText.Text = _viewModel.InstallMessage;
    }

    private void OnReinstallClick(object sender, RoutedEventArgs e) =>
        _viewModel?.ReinstallGsiConfigCommand.Execute(null);

}
