using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Import;

namespace Orrery.App.Import;

/// <summary>How the user chose to import. Null from the dialog means cancelled.</summary>
sealed record ImportChoice(bool IntoNewFolder, bool SkipAlreadyImported);

/// <summary>Shows what an import will add, and what it can't, before anything changes.</summary>
sealed class ImportPreviewWindow : Window
{
    public ImportPreviewWindow(ImportResult result, int alreadyImported, string credentialStoreName)
    {
        var report = result.Report;

        Title = "Import from mRemoteNG";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var newFolder = new RadioButton { GroupName = "dest", IsChecked = true, Content = $"In a new folder, \"{result.Root.Name}\"" };
        var topLevel = new RadioButton { GroupName = "dest", Content = "At the top level" };
        var skip = new CheckBox
        {
            Content = $"Skip {alreadyImported} connection{(alreadyImported == 1 ? "" : "s")} imported before",
            IsChecked = true,
            IsVisible = alreadyImported > 0,
        };

        var importButton = new Button { Content = "Import", IsDefault = true, Padding = new Thickness(16, 6), IsEnabled = report.Connections > 0 };
        importButton.Classes.Add("accent");
        importButton.Click += (_, _) => Close(new ImportChoice(newFolder.IsChecked == true, skip.IsChecked == true));
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        var details = new StackPanel { Spacing = 10 };
        AddList(details, "Skipped", report.Skipped, Dialogs.WarningBrush);
        AddList(details, "Not supported yet, so not imported", report.NotYetSupported, Dialogs.MutedBrush);
        AddList(details, "Warnings", report.Warnings, Dialogs.WarningBrush);

        var protocols = string.Join(" · ", report.Protocols.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}"));
        var credentials = report.CredentialsCreated == 0
            ? "No saved passwords in this file."
            : $"{report.CredentialsCreated} saved credential{(report.CredentialsCreated == 1 ? "" : "s")} will be added to {credentialStoreName}"
              + (report.PasswordsMerged > 0 ? $" ({report.PasswordsMerged} duplicate passwords merged)." : ".");

        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "Import from mRemoteNG", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = report.SourceFile, FontFamily = Dialogs.MonoFont, FontSize = 12, Foreground = Dialogs.MutedBrush },
                new TextBlock
                {
                    Text = $"{report.Connections} connection{(report.Connections == 1 ? "" : "s")} in {report.Folders} folder{(report.Folders == 1 ? "" : "s")}",
                    FontSize = 15,
                },
                new TextBlock { Text = protocols, Foreground = Dialogs.MutedBrush, IsVisible = protocols.Length > 0 },
                new TextBlock { Text = credentials, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "Only RDP connections can be opened for now; SSH, VNC and Telnet connections are kept for when they're supported.",
                    Foreground = Dialogs.MutedBrush,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = report.Protocols.Keys.Any(k => k != "RDP"),
                },
                new ScrollViewer { MaxHeight = 220, Content = details, IsVisible = details.Children.Count > 0 },
                Dialogs.Label("Put them"),
                newFolder,
                topLevel,
                skip,
                new TextBlock { Text = "Your mRemoteNG file isn't changed.", FontSize = 12, Foreground = Dialogs.MutedBrush },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, importButton } },
            },
        };
    }

    static void AddList(StackPanel panel, string title, IReadOnlyList<string> lines, IBrush brush)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Dialogs.Label(title));
        // One TextBlock a line and no virtualisation, so a long list is shortened first.
        foreach (var line in ImportReport.Shorten(lines))
        {
            list.Children.Add(new TextBlock { Text = "• " + line, Foreground = brush, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(list);
    }
}
