using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>
/// An SSH private key is a secret, so the box it's pasted into must not hand its text to UI Automation, which any
/// program reading the screen can use. These check the box and the credential editor around it.
/// </summary>
public class PrivateKeyBoxTests : IDisposable
{
    const string Body = "SECRETKEYBODYb64line1";

    static readonly string Key = $"-----BEGIN OPENSSH PRIVATE KEY-----\n{Body}\nSECRETKEYBODYb64line2\n-----END OPENSSH PRIVATE KEY-----\n";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Everything automation can say about a control and its parts, as one string to search.</summary>
    static string EverythingAutomationSays(Control control)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        var said = new List<string?> { peer.GetName(), peer.GetHelpText(), peer.GetItemStatus(), peer.GetLocalizedControlType(), peer.GetAcceleratorKey() };
        if (peer.GetProvider<IValueProvider>() is { } value)
        {
            said.Add(value.Value);
        }

        void Walk(AutomationPeer parent)
        {
            foreach (var child in parent.GetChildren())
            {
                said.Add(child.GetName());
                said.Add(child.GetHelpText());
                if (child.GetProvider<IValueProvider>() is { } childValue)
                {
                    said.Add(childValue.Value);
                }

                Walk(child);
            }
        }

        Walk(peer);
        return string.Join("\n", said.Where(s => s is not null));
    }

    [AvaloniaFact]
    public void A_plain_text_box_gives_automation_the_key_which_is_the_leak_this_box_closes()
    {
        var plain = new TextBox { AcceptsReturn = true, Text = Key };
        var window = new Window { Content = plain };
        window.Show();

        Assert.Contains(Body, EverythingAutomationSays(plain));
        window.Close();
    }

    [AvaloniaFact]
    public void The_private_key_box_tells_automation_only_how_many_lines_it_has()
    {
        var box = new PrivateKeyBox { Text = Key };
        AutomationProperties.SetName(box, "Private key");
        var window = new Window { Content = box };
        window.Show();
        var peer = ControlAutomationPeer.CreatePeerForElement(box);

        Assert.Equal("Private key, 4 lines", peer.GetProvider<IValueProvider>()!.Value);
        Assert.Equal("Private key", peer.GetName());
        Assert.Equal("private key", peer.GetLocalizedControlType());
        Assert.DoesNotContain("SECRET", EverythingAutomationSays(box));
        Assert.DoesNotContain("BEGIN", EverythingAutomationSays(box));
        window.Close();
    }

    [AvaloniaFact]
    public void Showing_the_key_on_screen_does_not_show_it_to_automation()
    {
        var box = new PrivateKeyBox { Text = Key };
        var window = new Window { Content = box };
        window.Show();
        Assert.False(box.Revealed);
        Assert.Equal(PasswordBox.Mask, box.PasswordChar);

        box.Revealed = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(box.Revealed);
        Assert.Equal(default, box.PasswordChar);
        Assert.DoesNotContain("SECRET", EverythingAutomationSays(box));
        window.Close();
    }

    [AvaloniaFact]
    public void Changes_raised_to_automation_carry_the_description_never_the_text()
    {
        var box = new PrivateKeyBox();
        var window = new Window { Content = box };
        window.Show();
        var peer = ControlAutomationPeer.CreatePeerForElement(box);
        var seen = new List<string>();
        peer.PropertyChanged += (_, e) => seen.AddRange(new[] { e.OldValue, e.NewValue }.OfType<string>());

        Assert.Equal("Private key, empty", peer.GetProvider<IValueProvider>()!.Value);
        box.Text = Key;
        Dispatcher.UIThread.RunJobs();
        box.Text = "one line";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Private key, 4 lines", seen);
        Assert.Contains("Private key, 1 line", seen);
        Assert.DoesNotContain(seen, s => s.Contains("SECRET") || s.Contains("one line"));
        window.Close();
    }

    [Theory]
    [InlineData(null, "Private key, empty")]
    [InlineData("", "Private key, empty")]
    [InlineData("one", "Private key, 1 line")]
    [InlineData("one\n", "Private key, 1 line")]
    [InlineData("a\r\nb\r\nc\r\n", "Private key, 3 lines")]
    public void The_description_counts_lines_and_nothing_else(string? text, string expected) =>
        Assert.Equal(expected, PrivateKeyBox.Describe(text));

    [AvaloniaFact]
    public void The_key_is_masked_by_default_and_cant_be_copied_out_while_masked()
    {
        var box = new PrivateKeyBox { Text = Key };
        var window = new Window { Content = box };
        window.Show();
        box.Focus();
        box.SelectAll();

        Assert.Equal(PasswordBox.Mask, box.PasswordChar);
        Assert.False(box.CanCopy);
        Assert.False(box.CanCut);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pasting_a_key_into_the_box_still_works()
    {
        var box = new PrivateKeyBox { Height = 120 };
        var window = new Window { Content = box, Width = 400, Height = 300 };
        window.Show();
        Assert.NotNull(window.Clipboard);
        await window.Clipboard!.SetTextAsync(Key);
        box.Focus();

        box.Paste();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Key, box.Text);
        Assert.Equal("Private key, 4 lines", ControlAutomationPeer.CreatePeerForElement(box).GetProvider<IValueProvider>()!.Value);
        // Don't leave a (throwaway) key on the clipboard.
        await window.Clipboard.ClearAsync();
        window.Close();
    }

    // ---- In the credential editor ----

    sealed class Editor : IDisposable
    {
        public required Window Window { get; init; }
        public required PrivateKeyBox Box { get; init; }
        public required ToggleButton Show { get; init; }
        public required CredentialsView View { get; init; }
        public required VaultFileStore Vault { get; init; }

        public void Dispose()
        {
            Window.Close();
            Vault.Dispose();
        }
    }

    async Task<Editor> OpenKeyEditorAsync()
    {
        Directory.CreateDirectory(_dir);
        var vault = new VaultFileStore(Path.Combine(_dir, "vault.frv"), new VaultFileStore.KdfSettings(8 * 1024, 1));
        await vault.UnlockAsync("a long test master password");
        var view = new CredentialsView(vault, () => Task.FromResult(true));
        var window = new Window { Content = view.Editor, Width = 500, Height = 900 };
        window.Show();

        var add = view.List.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "New credential");
        var list = new Window { Content = view.List };
        list.Show();
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        var kind = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Content, "SSH private key")));
        kind.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        list.Close();

        return new Editor
        {
            Window = window,
            View = view,
            Vault = vault,
            Box = window.GetVisualDescendants().OfType<PrivateKeyBox>().Single(),
            Show = window.GetVisualDescendants().OfType<ToggleButton>().Single(t => AutomationProperties.GetName(t) == "Show the private key on screen"),
        };
    }

    [AvaloniaFact]
    public async Task The_credential_editors_key_box_is_masked_with_a_show_button_and_hidden_from_automation()
    {
        using var editor = await OpenKeyEditorAsync();
        Assert.True(editor.Box.IsEffectivelyVisible);
        editor.Box.Text = Key;

        Assert.Equal(PasswordBox.Mask, editor.Box.PasswordChar);
        Assert.Equal("Show", editor.Show.Content);
        Assert.Equal("Private key", ControlAutomationPeer.CreatePeerForElement(editor.Box).GetName());
        Assert.DoesNotContain("SECRET", EverythingAutomationSays(editor.Box));
        // No text box in the editor's whole tree gives the key away, by any route.
        foreach (var box in editor.Window.GetVisualDescendants().OfType<TextBox>())
        {
            Assert.DoesNotContain("SECRET", EverythingAutomationSays(box));
        }

        editor.Show.IsChecked = true;
        Assert.True(editor.Box.Revealed);
        Assert.Equal("Hide", editor.Show.Content);
        Assert.DoesNotContain("SECRET", EverythingAutomationSays(editor.Box));

        editor.Show.IsChecked = false;
        Assert.False(editor.Box.Revealed);
        Assert.Equal("Show", editor.Show.Content);
        AccessibilityAudit.AssertAllNamed(editor.Window);
    }

    [AvaloniaFact]
    public async Task A_key_pasted_into_the_editor_is_saved_as_typed()
    {
        using var editor = await OpenKeyEditorAsync();
        var name = editor.Window.GetVisualDescendants().OfType<TextBox>().First(t => t is not PrivateKeyBox && t is not PasswordBox && !t.AcceptsReturn);
        name.Text = "test key";

        // The key text a test can make without storing a real one: SSH.NET must accept it for it to be saved.
        editor.Box.Text = TestPem.Value;
        var save = editor.Window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save"));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        var saved = Assert.Single(await editor.Vault.ListAsync());
        Assert.Equal("test key", saved.Name);
        Assert.Equal(CredentialKind.SshKey, saved.Kind);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", (await editor.Vault.GetAsync(saved.Id))!.Secret);
    }

    static readonly Lazy<string> TestPem = new(() => System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
