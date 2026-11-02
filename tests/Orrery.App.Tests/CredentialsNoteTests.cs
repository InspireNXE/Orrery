using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Credentials;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

public class CredentialsNoteTests
{
    sealed class NoVault : ICredentialStore
    {
        public bool IsLocked => true;
        public bool Exists => false;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }
        public Task<IReadOnlyList<CredentialSummary>> ListAsync() => Task.FromResult<IReadOnlyList<CredentialSummary>>([]);
        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    static List<string> Shown(Window window) => window.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Text != "CREDENTIALS").Select(t => t.Text!).ToList();

    // The empty-list note and the locked note both showed, one over the other, while the vault was being created.
    [AvaloniaFact]
    public async Task One_note_shows_at_a_time_and_says_theres_no_vault_yet()
    {
        var view = new CredentialsView(new NoVault(), () => Task.FromResult(false));
        var window = new Window { Content = view.List, Width = 300, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Shown(window));

        await view.ShowAsync(); // The user cancels creating the vault.
        Dispatcher.UIThread.RunJobs();
        var shown = Shown(window);
        Assert.Equal(2, shown.Count);
        Assert.Contains(shown, t => t.StartsWith("Saved credentials are kept in a vault", StringComparison.Ordinal));
        Assert.Contains("Create vault", shown);
        window.Close();
    }
}
