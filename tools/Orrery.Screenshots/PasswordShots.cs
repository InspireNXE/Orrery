using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;

namespace Orrery.Screenshots;

/// <summary>
/// The windows a saved command that types a password shows, for reviewing what they say: the editor in each place it
/// can be saved, the ask-first window and the vault unlock it can bring up. Not README pictures; written only where
/// asked (<c>--password-prompts &lt;folder&gt;</c>).
/// </summary>
static class PasswordShots
{
    sealed class DemoCredentials : ICredentialStore
    {
        public static readonly Guid Root = Guid.Parse("6f1d2a7e-1111-4c8e-9a51-7a0c4f0e0001");
        public bool IsLocked { get; set; }
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }
        public Task<IReadOnlyList<CredentialSummary>> ListAsync() => Task.FromResult<IReadOnlyList<CredentialSummary>>(
        [
            new(Root, "root (Production)", CredentialKind.Password, "root", null, null),
            new(Guid.Parse("6f1d2a7e-1111-4c8e-9a51-7a0c4f0e0002"), "riley", CredentialKind.Password, "riley", null, null),
        ]);
        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    public static async Task TakeAllAsync(string output)
    {
        var credentials = new DemoCredentials();
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Production" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.prod.example", Settings = { Protocol = Protocol.Ssh, Username = "riley" } };
        var console = new ConnectionEntry { Name = "core-switch", Host = "COM3", Settings = { Protocol = Protocol.Serial } };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        tree.Add(console, prod);

        // Become root on the connection itself: the risk, and the safer way.
        await EditorAsync(output, "1-editor-connection-become-root.png", new SavedCommandWindow(null, "web-01", credentials, web), w => w.BecomeRoot());

        // root's password chosen on a folder that has a serial console in it: who it reaches, and that it's unencrypted.
        await EditorAsync(output, "2-editor-folder-chosen-credential.png", new SavedCommandWindow(null, "Production", credentials, prod), async w =>
        {
            w.TextBox.Text = "su -";
            w.NameBox.Text = "Root shell";
            w.PasswordChoice = CommandPassword.Credential;
            await w.PasswordCredentialPicker!.RefreshAsync(DemoCredentials.Root);
        });

        // The same on the defaults for all connections: refused, and Save says why.
        await EditorAsync(output, "3-editor-defaults-chosen-credential.png",
            new SavedCommandWindow(null, "the defaults for all connections", credentials, tree.Root), async w =>
            {
                w.TextBox.Text = "su -";
                w.NameBox.Text = "Root shell";
                w.PasswordChoice = CommandPassword.Credential;
                await w.PasswordCredentialPicker!.RefreshAsync(DemoCredentials.Root);
                w.Save();
            });

        // The login password on the defaults: allowed, with where it reaches.
        await EditorAsync(output, "4-editor-defaults-login.png",
            new SavedCommandWindow(null, "the defaults for all connections", credentials, tree.Root), w => w.BecomeRoot());

        await ShowAsync(output, "5-ask-first.png", new ConfirmCommandWindow("Become root", "sudo -i", "web-01", "web-01.prod.example · Production",
            pressEnter: true, password: "If it asks for a password, Orrery types web-01's login password."));

        await ShowAsync(output, "6-vault-unlock.png", new VaultUnlockWindow(new DemoCredentials { IsLocked = true }, biometric: null,
            reason: "web-01 is asking for a password after \"Become root\". Unlock the vault for Orrery to type it, or cancel to type it yourself."));

        // The bar over a session, here after a fake prompt showed the password it was given.
        var session = new DemoTerminal(web.Host, Orrery.Core.Sessions.SessionState.Connected);
        var tab = new SessionTab(session, new TerminalView(session), new SessionOrigin("web-01", Protocol.Ssh, web.Id, () => Task.CompletedTask));
        var terminalWindow = new Window { Width = 900, Height = 360, Content = tab.Host };
        terminalWindow.Show();
        await SettleAsync();
        session.Output("riley@web-01:~$ sudo -i\r\nPassword: hunter2\r\nriley@web-01:~$ ");
        tab.Warning.Show("Security: the password from \"riley\" showed on screen after \"Become root\" on web-01, so that prompt wasn't a real "
            + "password prompt. It's in the scrollback. Change that password.", NoticeKind.Error);
        await SettleAsync();
        Save(terminalWindow, output, "7-session-warning-over-terminal.png");
        terminalWindow.Close();

        // Every message the bar can show, as worded in MainWindow.SavedCommands.cs, with demo names filled in.
        (string Text, NoticeKind Kind)[] messages =
        [
            ("Security: the password from \"riley\" showed on screen after \"Become root\" on web-01, so that prompt wasn't a real password prompt. It's in the scrollback and in this session's recording. Change that password.", NoticeKind.Error),
            ("web-01 didn't take the password from \"riley\", so it wasn't typed again. Type it yourself, or check the credential.", NoticeKind.Warning),
            ("No password was typed after \"Become root\" on web-01: the prompt asks for root's password, not riley's. Type it yourself if it's right.", NoticeKind.Warning),
            ("No password was typed after \"Become root\" on web-01: its login credential is for riley, but the session logged in as deploy. Type it yourself if it's right.", NoticeKind.Warning),
            ("No password prompt came after \"Become root\" on web-01, so no password was typed.", NoticeKind.Warning),
            ("\"Root shell\" didn't type a password on typed.example: this isn't a saved connection. Type it yourself if it asks.", NoticeKind.Warning),
            ("\"Root shell\" didn't type a password on web-01: a chosen credential's password isn't typed from the defaults for all connections. Type it yourself if it asks.", NoticeKind.Warning),
            ("web-01 didn't show a shell prompt, so \"Become root\", which types a password, wasn't sent, nor the commands after it.", NoticeKind.Warning),
            ("web-01 is asking for a password Orrery wasn't set to type, so the commands it sends when connected weren't sent.", NoticeKind.Warning),
            ("The password prompt after \"Become root\" on web-01 went away while the vault was unlocked, so no password was typed.", NoticeKind.Warning),
            ("web-01 has no saved login whose password \"Become root\" could type, so none was typed.", NoticeKind.Warning),
            ("\"Root shell\" answers with a credential that's gone or isn't a password, so none was typed.", NoticeKind.Warning),
            ("The vault stayed locked, so no password was typed after \"Become root\" on web-01. Type it yourself.", NoticeKind.Info),
        ];
        var list = new StackPanel { Spacing = 0 };
        foreach (var (text, kind) in messages)
        {
            var bar = new SessionWarningBar();
            bar.Show(text, kind);
            list.Children.Add(bar);
        }

        var all = new Window { Width = 900, SizeToContent = SizeToContent.Height, Content = new Border { Padding = new Avalonia.Thickness(0, 0, 0, 8), Child = list } };
        all.Bind(Window.BackgroundProperty, all.GetResourceObservable("OrreryBackground"));
        await ShowAsync(output, "8-session-warning-messages.png", all);
    }

    static Task EditorAsync(string output, string name, SavedCommandWindow window, Action<SavedCommandWindow> set) =>
        EditorAsync(output, name, window, w =>
        {
            set(w);
            return Task.CompletedTask;
        });

    static async Task EditorAsync(string output, string name, SavedCommandWindow window, Func<SavedCommandWindow, Task> set)
    {
        window.Show();
        await SettleAsync();
        await set(window);
        await SettleAsync();
        Save(window, output, name);
        window.Close();
    }

    static async Task ShowAsync(string output, string name, Window window)
    {
        window.Show();
        await SettleAsync();
        Save(window, output, name);
        window.Close();
    }

    static async Task SettleAsync()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
        }
    }

    static void Save(Window window, string output, string name)
    {
        using var image = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was drawn.");
        var path = Path.Combine(output, name);
        image.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"{path}: {image.PixelSize.Width}x{image.PixelSize.Height}");
    }
}
