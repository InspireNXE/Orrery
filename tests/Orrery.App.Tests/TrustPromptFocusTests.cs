using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.App.Security;
using Orrery.Core.Security;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>
/// A trust prompt that opens over a session has the focus and is read out first, so a screen reader user hears the
/// question rather than the terminal behind it.
/// </summary>
public class TrustPromptFocusTests
{
    public static TheoryData<string, string> Prompts => new()
    {
        { "hostkey-new", "Trust and connect" },
        { "hostkey-changed", "Cancel" },
        { "certificate-new", "Trust and connect" },
        { "certificate-changed", "Cancel" },
    };

    static Window Prompt(string which) => which switch
    {
        "hostkey-new" => new HostKeyPromptWindow(new HostKeyPrompt(CertificatePromptKind.Unknown, "ssh.example", 22, "ssh-ed25519", "SHA256:abc", null)),
        "hostkey-changed" => new HostKeyPromptWindow(new HostKeyPrompt(CertificatePromptKind.Changed, "ssh.example", 22, "ssh-ed25519", "SHA256:abc", "SHA256:def")),
        "certificate-new" => new CertificatePromptWindow(new CertificatePrompt(CertificatePromptKind.Unknown, "ssh.example", 3389, "CN=rdp", "CN=ca", DateTime.Now, DateTime.Now.AddYears(1), "AB12", null, [])),
        _ => new CertificatePromptWindow(new CertificatePrompt(CertificatePromptKind.Changed, "ssh.example", 3389, "CN=rdp", "CN=ca", DateTime.Now, DateTime.Now.AddYears(1), "AB12", "CD34", [])),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Prompts))]
    public void Opens_with_its_safe_button_focused_and_reads_its_question(string which, string focusedButton)
    {
        var owner = new Window();
        owner.Show();
        var spoken = new List<string>();
        void Heard(string text) => spoken.Add(text);
        ScreenReader.Announced += Heard;
        try
        {
            var prompt = Prompt(which);
            _ = prompt.ShowDialog<CertificateTrustDecision>(owner);
            Dispatcher.UIThread.RunJobs();

            var focused = Assert.IsType<Button>(prompt.FocusManager?.GetFocusedElement());
            Assert.Equal(focusedButton, focused.Content);
            Assert.Contains(spoken, t => t.Contains("ssh.example") && t.Length > 60);
            Assert.Single(owner.OwnedWindows);
            prompt.Close();
        }
        finally
        {
            ScreenReader.Announced -= Heard;
            owner.Close();
        }
    }
}
