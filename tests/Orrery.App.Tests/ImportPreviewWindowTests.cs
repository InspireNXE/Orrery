using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Import;
using Orrery.Core.Connections;
using Orrery.Core.Import;
using Xunit;

namespace Orrery.App.Tests;

public class ImportPreviewWindowTests
{
    [AvaloniaFact]
    public void A_long_skipped_list_shows_the_first_500_lines_and_a_count_of_the_rest()
    {
        var skipped = Enumerable.Range(0, 600).Select(i => $"raw{i}: RAW isn't supported").ToList();
        var report = new ImportReport("big.xml", 0, 1, 0, 0, new Dictionary<string, int> { ["RDP"] = 1 }, skipped, [], []);
        var window = new ImportPreviewWindow(new ImportResult(new ConnectionFolder { Name = "Imported" }, [], report), 0, "your vault");
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text?.StartsWith("• ") == true).ToList();
        Assert.Equal(501, rows.Count);
        Assert.Contains("100 more", rows[^1].Text);
        window.Close();
    }
}
