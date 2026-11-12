using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Desktop.Services;

namespace EngineeringWorkspace.Desktop.Pages.Credentials;

/// <summary>
/// The organization key: create it once (administrator), enter it on each other computer, or show it to hand it on.
/// Used on Credential Management and in setup. The key is shown only after an explicit confirmation and never logged.
/// </summary>
public sealed partial class OrganizationKeyPanel(IOrganizationKeyService keys, IDialogService dialogs) : ObservableObject
{
    [ObservableProperty] private OrganizationKeyState _state = OrganizationKeyState.Unavailable;
    [ObservableProperty] private string _stateText = "Checking...";
    [ObservableProperty] private string _enteredKey = string.Empty;
    [ObservableProperty] private string? _shownKey;
    [ObservableProperty] private string? _message;

    public bool CanCreate => State == OrganizationKeyState.NotCreated;
    public bool CanEnter => State == OrganizationKeyState.NotOnThisComputer;
    public bool CanShow => State == OrganizationKeyState.Ready;
    public bool IsReady => State == OrganizationKeyState.Ready;
    public bool HasShownKey => !string.IsNullOrEmpty(ShownKey);

    partial void OnStateChanged(OrganizationKeyState value)
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanEnter));
        OnPropertyChanged(nameof(CanShow));
        OnPropertyChanged(nameof(IsReady));
    }

    partial void OnShownKeyChanged(string? value) => OnPropertyChanged(nameof(HasShownKey));

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            State = await keys.GetStateAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            State = OrganizationKeyState.Unavailable;
            Message = exception.Message;
        }

        StateText = State switch
        {
            OrganizationKeyState.Ready => $"This computer holds organization key {keys.KeyId}: tenant secrets are shared securely with every computer that holds it.",
            OrganizationKeyState.NotOnThisComputer =>
                "The database has an organization key, but this computer does not. Enter the key from your administrator to use the customers' tenant connections here.",
            OrganizationKeyState.NotCreated =>
                "No organization key yet. Create it once: tenant secrets are then stored in the database, encrypted, and every technician who enters the key can use them.",
            _ => "The organization key is available once the database is ready."
        };
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!dialogs.Confirm(new ConfirmationRequest("Create Organization Key",
                "Create the organization key for this database? It encrypts every tenant secret stored in the database. Keep a copy somewhere safe (for example your password manager): without it, stored secrets cannot be read.",
                [new("Database", "Current SecurePlus database")], "Create Key")))
            return;
        try
        {
            ShownKey = await keys.CreateAsync();
            Message = "Key created. Copy it or save it to a file now and keep it safe; give it to technicians who need tenant access.";
        }
        catch (InvalidOperationException exception)
        {
            Message = exception.Message;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task EnterAsync()
    {
        Message = await keys.EnrolAsync(EnteredKey) ?? "Key accepted. Tenant secrets stored in the database can now be used on this computer.";
        EnteredKey = string.Empty;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ShowAsync()
    {
        if (!dialogs.Confirm(new ConfirmationRequest("Show Organization Key",
                "Show the organization key so you can give it to another computer? Anyone with the key and access to the database can read tenant secrets.",
                [new("Computer", Environment.MachineName)], "Show Key")))
            return;
        ShownKey = await keys.ExportAsync();
    }

    [RelayCommand]
    private void Copy()
    {
        if (ShownKey is null) return;
        Clipboard.SetText(ShownKey);
        Message = "Copied. Paste it into Secure Plus on the other computer (Credential Management or setup), then clear your clipboard.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (ShownKey is null) return;
        var path = dialogs.SaveFile("SecurePlus-organization-key.txt", "Text (*.txt)|*.txt");
        if (path is null) return;
        await File.WriteAllTextAsync(path, $"Secure Plus organization key ({keys.KeyId}). Keep this file secret.\r\n{ShownKey}\r\n");
        Message = $"Saved to {path}. Store it somewhere safe and delete other copies.";
    }

    [RelayCommand]
    private void Hide() => ShownKey = null;
}
