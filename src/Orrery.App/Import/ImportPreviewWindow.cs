using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Import;

namespace Orrery.App.Import;

/// <summary>How the user chose to import. Null from the dialog means canceled.</summary>
sealed record ImportChoice(bool IntoNewFolder, bool SkipAlreadyImported, bool KeepCommands = false);

/// <summary>Shows what an import will add, and what it can't, before anything changes.</summary>
sealed class ImportPreviewWindow : Window
{
    public ImportPreviewWindow(ImportResult result, int alreadyImported, string credentialStoreName)
    {
        var report = result.Report;

        Title = $"Import from {report.From}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var newFolder = new RadioButton { GroupName = "dest", IsChecked = true };
        var topLevel = new RadioButton { GroupName = "dest" };
        var placement = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Dialogs.Label("Put them"),
                Dialogs.Choice(newFolder, "In a new folder", result.Root.Name),
                Dialogs.Choice(topLevel, "At the top level", "Beside your other connections and folders"),
            },
        };
        var skip = new CheckBox
        {
            Content = $"Skip {alreadyImported} connection{(alreadyImported == 1 ? "" : "s")} imported before",
            IsChecked = true,
            IsVisible = alreadyImported > 0,
        };

        // Commands are typed into terminals, so a file from someone else doesn't bring them in unless asked.
        var commands = ImportReview.Commands(result.Root);
        var keepCommands = new CheckBox
        {
            Content = $"Also import {commands.Count} saved command{(commands.Count == 1 ? "" : "s")}",
            IsChecked = false,
            IsVisible = commands.Count > 0,
        };
        var commandList = new StackPanel { Spacing = 4, IsVisible = commands.Count > 0 };
        commandList.Children.Add(Dialogs.Label("Saved commands in this file, sent to a terminal only when you pick one"));
        foreach (var (owner, command) in commands.Take(MaxCommandsListed))
        {
            commandList.Children.Add(new TextBlock { Text = $"{command.Name} · {owner.Name}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            commandList.Children.Add(new TextBlock
            {
                Text = command.Text.Length > 300 ? command.Text[..297] + "…" : command.Text,
                FontFamily = Dialogs.MonoFont,
                FontSize = 12,
                Foreground = Dialogs.MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 0, 0, 4),
            });
        }

        if (commands.Count > MaxCommandsListed)
        {
            commandList.Children.Add(new TextBlock { Text = $"and {commands.Count - MaxCommandsListed} more", FontSize = 12, Foreground = Dialogs.MutedBrush });
        }

        var importButton = new Button { Content = "Import", IsDefault = true, Padding = new Thickness(16, 6), IsEnabled = report.Connections > 0 };
        importButton.Classes.Add("accent");
        importButton.Click += (_, _) => Close(new ImportChoice(newFolder.IsChecked == true, skip.IsChecked == true, keepCommands.IsChecked == true));
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        var details = new StackPanel { Spacing = 10 };
        AddList(details, "Skipped", report.Skipped, Dialogs.WarningBrush);
        AddList(details, "Not supported yet, so not imported", report.NotYetSupported, Dialogs.MutedBrush);
        AddList(details, "Warnings", report.Warnings, Dialogs.WarningBrush);
        details.Children.Add(commandList);

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
                new TextBlock { Text = $"Import from {report.From}", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = report.SourceFile, FontFamily = Dialogs.MonoFont, FontSize = 12, Foreground = Dialogs.MutedBrush },
                new TextBlock
                {
                    Text = $"{report.Connections} connection{(report.Connections == 1 ? "" : "s")} in {report.Folders} folder{(report.Folders == 1 ? "" : "s")}",
                    FontSize = 15,
                },
                new TextBlock { Text = protocols, Foreground = Dialogs.MutedBrush, IsVisible = protocols.Length > 0 },
                new TextBlock { Text = credentials, TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { MaxHeight = 220, Content = details, IsVisible = details.Children.Count > 0 },
                placement,
                skip,
                keepCommands,
                new TextBlock { Text = $"Your {report.From} file isn't changed.", FontSize = 12, Foreground = Dialogs.MutedBrush },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, importButton } },
            },
        };
    }

    const int MaxCommandsListed = 20;

    static void AddList(StackPanel panel, string title, IReadOnlyList<string> lines, IBrush brush)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Dialogs.Label(title));
        // One TextBlock a line and no virtualization, so a long list is shortened first.
        foreach (var line in ImportReport.Shorten(lines))
        {
            list.Children.Add(new TextBlock { Text = "• " + line, Foreground = brush, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(list);
    }
}
