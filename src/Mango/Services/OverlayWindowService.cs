using Mango.Interop;
using Mango.Views.Overlay;

namespace Mango.Services;

public sealed class OverlayWindowService
{
    private readonly AppSettings _settings;
    private OverlayWindow? _overlayWindow;

    public OverlayWindowService(AppSettings settings)
    {
        _settings = settings;
    }

    public OverlayWindow EnsureOverlay()
    {
        if (_overlayWindow is not null)
        {
            return _overlayWindow;
        }

        _overlayWindow = new OverlayWindow();
        ConfigureOverlayBounds();
        return _overlayWindow;
    }

    public void ShowOverlay()
    {
        var window = EnsureOverlay();
        var clickThrough = !_settings.OverlayInteractive;

        ConfigureOverlayBounds();
        window.AppWindow.Show();
        window.RefreshTimerDisplays();
        ApplyNativeState(window, clickThrough, topmost: true);
        ScheduleNativeStateRefresh(window, clickThrough, topmost: true);

        _settings.OverlayVisible = true;
        _settings.Save();
    }

    public void HideOverlay()
    {
        if (_overlayWindow is null)
        {
            _settings.OverlayVisible = false;
            _settings.Save();
            return;
        }

        OverlayClickThroughHelper.Reset(_overlayWindow);
        _overlayWindow.AppWindow.Hide();
        _settings.OverlayVisible = false;
        _settings.Save();
    }

    public void ToggleOverlayVisibility()
    {
        if (_settings.OverlayVisible)
        {
            HideOverlay();
        }
        else
        {
            ShowOverlay();
        }
    }

    public void SetInteractive(bool interactive)
    {
        _settings.OverlayInteractive = interactive;
        _settings.Save();

        if (_overlayWindow is null)
        {
            return;
        }

        var clickThrough = !interactive;
        _overlayWindow.SetInteractiveMode(interactive);
        ApplyNativeState(_overlayWindow, clickThrough, topmost: _settings.OverlayVisible);
        ScheduleNativeStateRefresh(_overlayWindow, clickThrough, topmost: _settings.OverlayVisible);
    }

    public void ApplySettings()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        _overlayWindow.ApplySettingsFromStore();
        if (_settings.OverlayVisible)
        {
            ApplyNativeState(_overlayWindow, clickThrough: !_settings.OverlayInteractive, topmost: true);
        }
    }

    public void ResetPanelPositions()
    {
        if (_overlayWindow is not null)
        {
            _overlayWindow.ResetPanelPositions();
            return;
        }

        _settings.PanelLayout = new PanelLayoutSettings();
        _settings.Save();
    }

    public void ToggleInteractive() => SetInteractive(!_settings.OverlayInteractive);

    public void RecreateIfVisible()
    {
        if (!_settings.OverlayVisible)
        {
            return;
        }

        var interactive = _settings.OverlayInteractive;
        if (_overlayWindow is not null)
        {
            OverlayClickThroughHelper.Reset(_overlayWindow);
            _overlayWindow.Close();
            _overlayWindow = null;
        }

        ShowOverlay();
        SetInteractive(interactive);
    }

    public void Close()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        OverlayClickThroughHelper.Reset(_overlayWindow);
        _overlayWindow.Close();
        _overlayWindow = null;
        _settings.OverlayVisible = false;
        _settings.OverlayInteractive = false;
    }

    private static void ApplyNativeState(OverlayWindow window, bool clickThrough, bool topmost)
    {
        window.ConfigureNativeWindow(clickThrough, topmost);
    }

    private static void ScheduleNativeStateRefresh(OverlayWindow window, bool clickThrough, bool topmost)
    {
        // Defer until layout has ActualWidth/ActualHeight for region math.
        window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ApplyNativeState(window, clickThrough, topmost);
            window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                ApplyNativeState(window, clickThrough, topmost));
        });
    }

    private void ConfigureOverlayBounds()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            _overlayWindow.AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

        if (displayArea is not null)
        {
            // OuterBounds covers the full monitor, including the taskbar strip.
            // WorkArea would clip widgets so they cannot sit over the taskbar.
            _overlayWindow.AppWindow.MoveAndResize(displayArea.OuterBounds);
        }

        _overlayWindow.AppWindow.IsShownInSwitchers = false;

        var presenter = _overlayWindow.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = false;
        }
    }
}
