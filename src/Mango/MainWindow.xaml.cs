using Mango.Interop;
using Mango.Services;
using Mango.Views.Shell;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Mango;

public sealed partial class MainWindow : Window
{
    private WindowMessageHook? _messageHook;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTheme();
        AppWindow.SetIcon("Assets/AppIcon.ico");
        Title = "Mango";

        RootFrame.Navigate(typeof(ShellPage));

        Closed += OnClosed;

        var hwnd = Win32WindowHelper.GetHandle(this);
        _messageHook = new WindowMessageHook(hwnd, (msg, wParam, lParam) =>
        {
            AppServices.HotkeyService.TryHandleHotkeyMessage(hwnd, msg, wParam);
        });

        AppServices.HotkeyService.Register(this);
    }

    internal void ReloadAfterHotReload()
    {
        if (RootFrame.Content is ShellPage shell)
        {
            shell.ReloadCurrentPage();
        }
    }

    internal void ApplyTheme()
    {
        LayoutRoot.RequestedTheme = AppServices.Settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = AppServices.Settings.Theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        AppServices.Shutdown();
    }
}
