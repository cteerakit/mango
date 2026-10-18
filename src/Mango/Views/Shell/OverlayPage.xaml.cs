using Mango.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class OverlayPage : Page
{
    private ShellViewModel? _viewModel;
    private bool _suppressEvents;

    public OverlayPage()
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
        _suppressEvents = true;
        PlayerToggle.IsOn = viewModel.ShowPlayerPanel;
        BountyTimerToggle.IsOn = viewModel.ShowBountyTimer;
        PowerTimerToggle.IsOn = viewModel.ShowPowerTimer;
        WisdomTimerToggle.IsOn = viewModel.ShowWisdomTimer;
        LotusTimerToggle.IsOn = viewModel.ShowLotusTimer;
        OpacitySlider.Value = viewModel.OverlayOpacity;
        RuneLeadNumberBox.Value = viewModel.RuneTimerLeadSeconds;
        TurboToggle.IsOn = viewModel.IsTurboMode;
        _suppressEvents = false;

        PlayerToggle.Toggled += OnSettingChanged;
        BountyTimerToggle.Toggled += OnSettingChanged;
        PowerTimerToggle.Toggled += OnSettingChanged;
        WisdomTimerToggle.Toggled += OnSettingChanged;
        LotusTimerToggle.Toggled += OnSettingChanged;
        OpacitySlider.ValueChanged += OnOpacityChanged;
        RuneLeadNumberBox.ValueChanged += OnRuneLeadChanged;
        TurboToggle.Toggled += OnSettingChanged;
    }

    private void OnOpacityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        ApplySettings();
    }

    private void OnRuneLeadChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressEvents || double.IsNaN(args.NewValue))
        {
            return;
        }

        ApplySettings();
    }

    private void OnShowOverlayClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        _viewModel?.ShowOverlayCommand.Execute(null);

    private void OnHideOverlayClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        _viewModel?.HideOverlayCommand.Execute(null);

    private void OnResetPositionsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ResetPanelPositionsCommand.Execute(null);
        ResetPositionsStatus.Text = _viewModel.PanelLayoutStatusMessage;
        ResetPositionsStatus.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void OnSettingChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        ApplySettings();
    }

    private void ApplySettings()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ShowPlayerPanel = PlayerToggle.IsOn;
        _viewModel.ShowBountyTimer = BountyTimerToggle.IsOn;
        _viewModel.ShowPowerTimer = PowerTimerToggle.IsOn;
        _viewModel.ShowWisdomTimer = WisdomTimerToggle.IsOn;
        _viewModel.ShowLotusTimer = LotusTimerToggle.IsOn;
        _viewModel.OverlayOpacity = OpacitySlider.Value;
        _viewModel.RuneTimerLeadSeconds = (int)RuneLeadNumberBox.Value;
        _viewModel.IsTurboMode = TurboToggle.IsOn;
        _viewModel.SaveSettingsCommand.Execute(null);
    }
}
