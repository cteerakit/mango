using Mango.Models;
using Mango.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Mango.Views.Shell;

public sealed partial class SettingsPage : Page
{
    private bool _notesTransferBusy;
    private bool _suppressSteamKeyChange;
    private bool _suppressStratzTokenChange;
    private readonly AppUpdateService _updates = new();

    public SettingsPage()
    {
        InitializeComponent();
        ShellContentLayout.Attach(LayoutRoot, ContentColumn);
        ThemeSelector.SelectedItem = ThemeSelector.Items.OfType<RadioButton>()
            .First(item => (string)item.Tag == AppServices.Settings.Theme.ToString());
        ThemeSelector.SelectionChanged += OnThemeSelectionChanged;
        LoadSteamApiKey();
        LoadStratzApiToken();
        AppVersionText.Text = $"Version {_updates.Version}";
        AppUpdateStatus.Text = _updates.Status;
        CheckForUpdatesButton.IsEnabled = _updates.CanCheckForUpdates;
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        AppUpdateStatus.Text = "Checking for updates…";
        try
        {
            AppUpdateStatus.Text = await _updates.CheckForUpdatesAsync();
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = _updates.CanCheckForUpdates;
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedItem is RadioButton { Tag: string theme }
            && Enum.TryParse<AppTheme>(theme, out var preference))
        {
            AppServices.Settings.Theme = preference;
            ((MainWindow)App.Window).ApplyTheme();
            AppServices.Settings.Save();
        }
    }

    private void LoadSteamApiKey()
    {
        _suppressSteamKeyChange = true;
        SteamApiKeyBox.Password = AppServices.Settings.SteamApiKey;
        _suppressSteamKeyChange = false;
        SteamApiKeyStatus.Text = string.IsNullOrWhiteSpace(AppServices.Settings.SteamApiKey)
            ? "No Steam API key saved."
            : "Steam API key saved on this PC.";
    }

    private void OnSteamApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSteamKeyChange)
        {
            return;
        }

        AppServices.Settings.SteamApiKey = SteamApiKeyBox.Password.Trim();
        AppServices.Settings.Save();
        SteamApiKeyStatus.Text = string.IsNullOrWhiteSpace(AppServices.Settings.SteamApiKey)
            ? "Steam API key cleared."
            : "Steam API key saved.";
    }

    private void LoadStratzApiToken()
    {
        _suppressStratzTokenChange = true;
        StratzApiTokenBox.Password = AppServices.Settings.StratzApiToken;
        _suppressStratzTokenChange = false;
        StratzApiTokenStatus.Text = string.IsNullOrWhiteSpace(AppServices.Settings.StratzApiToken)
            ? "No STRATZ token saved. Positions are estimated."
            : "STRATZ token saved on this PC.";
    }

    private void OnStratzApiTokenChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressStratzTokenChange)
        {
            return;
        }

        AppServices.Settings.StratzApiToken = StratzApiTokenBox.Password.Trim();
        AppServices.Settings.Save();
        StratzApiTokenStatus.Text = string.IsNullOrWhiteSpace(AppServices.Settings.StratzApiToken)
            ? "STRATZ token cleared. Positions are estimated."
            : "STRATZ token saved.";
    }

    private void OnViewNotesClick(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(NotesListPage));
    }

    private async void OnExportNotesClick(object sender, RoutedEventArgs e)
    {
        if (_notesTransferBusy)
        {
            return;
        }

        _notesTransferBusy = true;
        try
        {
            var notes = AppServices.Settings.PlayerNotes;
            var picker = new FileSavePicker(App.Window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "mango-player-notes",
                DefaultFileExtension = ".json",
                CommitButtonText = "Export notes",
            };
            picker.FileTypeChoices.Add("Player notes", [".json"]);

            var result = await picker.PickSaveFileAsync();
            if (result is null)
            {
                return;
            }

            await File.WriteAllTextAsync(result.Path, PlayerNotesTransfer.Serialize(notes));
            ShowNotesTransferStatus($"Exported {CountNotes(notes)} note(s) to {Path.GetFileName(result.Path)}.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not export notes", ex.Message);
        }
        finally
        {
            _notesTransferBusy = false;
        }
    }

    private async void OnImportNotesClick(object sender, RoutedEventArgs e)
    {
        if (_notesTransferBusy)
        {
            return;
        }

        _notesTransferBusy = true;
        try
        {
            var picker = new FileOpenPicker(App.Window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "Import notes",
            };
            picker.FileTypeFilter.Add(".json");

            var result = await picker.PickSingleFileAsync();
            if (result is null)
            {
                return;
            }

            var json = await File.ReadAllTextAsync(result.Path);
            if (!PlayerNotesTransfer.TryParse(json, out var imported, out var error))
            {
                await ShowMessageAsync("Could not import notes", error);
                return;
            }

            if (imported.Count == 0)
            {
                await ShowMessageAsync("Could not import notes", "That file didn't contain any notes.");
                return;
            }

            var existing = AppServices.Settings.PlayerNotes;
            var replace = false;
            if (existing.Count > 0)
            {
                var choice = await ShowImportModeDialogAsync(existing.Count, imported.Count);
                if (choice is null)
                {
                    return;
                }

                replace = choice.Value;
            }

            var merge = PlayerNotesTransfer.Apply(existing, imported, replace);
            AppServices.Settings.Save();
            ShowNotesTransferStatus(DescribeImport(merge, replace));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not import notes", ex.Message);
        }
        finally
        {
            _notesTransferBusy = false;
        }
    }

    private async Task<bool?> ShowImportModeDialogAsync(int existingCount, int importedCount)
    {
        var dialog = new ContentDialog
        {
            Title = "Import player notes",
            Content = $"You already have {existingCount} note(s). Merge updates matching players and keeps the rest. Replace discards current notes, then imports {importedCount}.",
            PrimaryButtonText = "Merge",
            SecondaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => false,
            ContentDialogResult.Secondary => true,
            _ => null,
        };
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        await dialog.ShowAsync();
    }

    private void ShowNotesTransferStatus(string message)
    {
        NotesTransferStatus.Text = message;
        NotesTransferStatus.Visibility = Visibility.Visible;
    }

    private static int CountNotes(IReadOnlyDictionary<string, PlayerNote> notes) =>
        notes.Count(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { HasSavedData: true });

    private static string DescribeImport(PlayerNotesMergeResult result, bool replace)
    {
        if (replace)
        {
            return $"Replaced notes with {result.Total} imported note(s).";
        }

        return $"Imported notes: {result.Added} added, {result.Updated} updated.";
    }
}
