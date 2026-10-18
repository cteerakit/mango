using Mango.Models;
using Mango.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class NotesListPage : Page
{
    private bool _dialogOpen;

    public NotesListPage()
    {
        InitializeComponent();
        ShellContentLayout.Attach(LayoutRoot, ContentColumn);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BindNotes();
    }

    private void BindNotes()
    {
        var notes = AppServices.Settings.PlayerNotes.Values
            .Where(note => note.HasSavedData)
            .OrderBy(note => note.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(note => note.DisplayId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        NotesList.ItemsSource = notes;
        NotesList.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Text = notes.Count == 0
            ? "Saved player notes."
            : notes.Count == 1
                ? "1 saved note."
                : $"{notes.Count} saved notes.";
    }

    private async void OnEditNoteClick(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || sender is not Button { DataContext: PlayerNote note })
        {
            return;
        }

        var input = new TextBox
        {
            AcceptsReturn = true,
            Height = 140,
            Text = note.Content,
            TextWrapping = TextWrapping.Wrap,
        };

        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(note.PlayerName) ? "Edit note" : $"Edit note — {note.PlayerName}",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            PlayerNotesTransfer.Upsert(
                AppServices.Settings.PlayerNotes,
                note.PlayerName,
                note.PlayerId,
                input.Text.Trim());
            AppServices.Settings.Save();
            BindNotes();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void OnDeleteNoteClick(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || sender is not Button { DataContext: PlayerNote note })
        {
            return;
        }

        var label = !string.IsNullOrWhiteSpace(note.PlayerName)
            ? note.PlayerName
            : !string.IsNullOrWhiteSpace(note.PlayerId)
                ? note.PlayerId
                : "this player";

        var dialog = new ContentDialog
        {
            Title = "Delete note",
            Content = $"Delete the note for {label}? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var key = note.StorageKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            AppServices.Settings.PlayerNotes.Remove(key);
            AppServices.Settings.Save();
            BindNotes();
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
