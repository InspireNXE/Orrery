using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

public class NewPasswordTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void A_new_vault_states_the_rules_and_turns_down_a_weak_password()
    {
        Directory.CreateDirectory(_dir);
        using var vault = new VaultFileStore(Path.Combine(_dir, "vault.frv"), new VaultFileStore.KdfSettings(8 * 1024, 1));
        var window = new VaultUnlockWindow(vault);
        window.Show();
        try
        {
            var texts = () => window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(PasswordRules.Requirements, texts());

            var boxes = window.GetVisualDescendants().OfType<PasswordBox>().ToList();
            boxes[0].Text = boxes[1].Text = "Password2025!";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(texts(), t => t?.StartsWith("Too weak.") == true);

            window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Create vault")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vault.Exists);
            Assert.Contains("common password", window.GetVisualDescendants().OfType<Notice>().Single(n => n.IsVisible && n.Kind == NoticeKind.Error).Text);

            boxes[0].Text = boxes[1].Text = "granite lantern oboe quietly";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Strong", texts());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_vault_made_before_the_rules_still_unlocks_with_its_short_password()
    {
        Directory.CreateDirectory(_dir);
        using var vault = new VaultFileStore(Path.Combine(_dir, "vault.frv"), new VaultFileStore.KdfSettings(8 * 1024, 1));
        await vault.UnlockAsync("hunter22");
        vault.Lock();
        var window = new VaultUnlockWindow(vault);
        window.Show();
        try
        {
            Assert.Empty(window.GetVisualDescendants().OfType<PasswordMeter>());
            window.GetVisualDescendants().OfType<PasswordBox>().First().Text = "hunter22";
            window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Unlock")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (vault.IsLocked)
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out");
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(50);
            }
        }
        finally
        {
            window.Close();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
