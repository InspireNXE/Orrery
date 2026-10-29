using Avalonia.Controls;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>
/// A dropdown of saved credentials. While the vault is locked it offers an "unlock" item instead, and
/// choosing it shows the unlock dialog and then the real list.
/// </summary>
sealed class CredentialPicker : ComboBox
{
    const string UnlockTag = "unlock";

    readonly ICredentialStore _store;
    readonly string _noneLabel;
    bool _refreshing;

    public CredentialPicker(ICredentialStore store, string noneLabel = "None")
    {
        _store = store;
        _noneLabel = noneLabel;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        SelectionChanged += async (_, _) => await OnSelectionChangedAsync();
    }

    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <summary>Raised when the user picks a different entry (not when the list is rebuilt).</summary>
    public event EventHandler? ChoiceChanged;

    /// <summary>The chosen credential, or null for none.</summary>
    public Guid? SelectedId => (SelectedItem as ComboBoxItem)?.Tag is Guid id ? id : null;

    /// <summary>The chosen credential's details, when the vault is unlocked.</summary>
    public CredentialSummary? SelectedSummary => (SelectedItem as ComboBoxItem)?.DataContext as CredentialSummary;

    public async Task RefreshAsync(Guid? select)
    {
        _refreshing = true;
        try
        {
            var items = new List<ComboBoxItem> { new() { Content = _noneLabel } };
            if (_store.IsLocked)
            {
                if (select is { } id)
                {
                    items.Add(new ComboBoxItem { Content = "Saved credential (vault locked)", Tag = id });
                }

                items.Add(new ComboBoxItem { Content = _store.Exists ? "Unlock vault to choose…" : "Create a vault to save credentials…", Tag = UnlockTag });
            }
            else
            {
                var summaries = await _store.ListAsync();
                items.AddRange(summaries.Select(c => new ComboBoxItem
                {
                    Content = c.Username is null ? c.Name : $"{c.Name}  ({c.DisplayUser})",
                    Tag = c.Id,
                    DataContext = c,
                }));

                if (select is { } id && summaries.All(c => c.Id != id))
                {
                    items.Add(new ComboBoxItem { Content = "Missing credential (deleted?)", Tag = id });
                }
            }

            ItemsSource = items;
            SelectedItem = items.FirstOrDefault(i => i.Tag is Guid g && g == select) ?? items[0];
        }
        finally
        {
            _refreshing = false;
        }
    }

    async Task OnSelectionChangedAsync()
    {
        if (_refreshing)
        {
            return;
        }

        if ((SelectedItem as ComboBoxItem)?.Tag is UnlockTag)
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            bool unlocked = owner is not null && await VaultUnlockWindow.EnsureUnlockedAsync(_store, owner);
            await RefreshAsync(select: null);
            if (unlocked)
            {
                IsDropDownOpen = true;
            }

            return;
        }

        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }
}
