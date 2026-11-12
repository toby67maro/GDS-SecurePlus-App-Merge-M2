using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Security;

namespace EngineeringWorkspace.Desktop.Pages.Credentials;

/// <summary>A credential row: metadata and status only. The secret is never available to the view.</summary>
public sealed record CredentialRow(
    string CredentialId,
    string DisplayName,
    CredentialType CredentialType,
    string Provider,
    string SecretReference,
    string? Customer,
    string? Tenant,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpirationDateUtc,
    CredentialStatus Status,
    string StatusMessage)
{
    /// <summary>Saved secrets are only ever shown as a mask.</summary>
    public string Secret => Status is CredentialStatus.Missing ? "Missing" : "********";

    public static CredentialRow From(CredentialView view) => new(view.Metadata.CredentialId, view.Metadata.DisplayName, view.Metadata.CredentialType,
        view.Metadata.Provider, view.Metadata.SecretReference.ToString(), view.Metadata.AssociatedCustomerId, view.Metadata.AssociatedTenantId,
        view.Metadata.CreatedAtUtc, view.Metadata.UpdatedAtUtc, view.Metadata.ExpirationDateUtc, view.Status, view.StatusMessage);
}

/// <summary>Workspace → Settings → Credential Management. Add, Replace, Delete (with confirmation) and Validate. No Show Secret.</summary>
public sealed partial class CredentialManagementViewModel(ICredentialService credentials, IDialogService dialogs, CredentialSettings settings, OrganizationKeyPanel organizationKey) : PageViewModel
{
    public event EventHandler? SecretInputsCleared;

    public ObservableCollection<CredentialRow> Credentials { get; } = [];
    public IReadOnlyList<CredentialType> CredentialTypes { get; } = Enum.GetValues<CredentialType>();
    public string SecretStoreName => credentials.SecretStoreName;
    public string ExpirationPolicy => $"Credentials expiring within {settings.ExpirationWarningDays} days are flagged Expires Soon ([Credentials] ExpirationWarningDays).";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand), nameof(DeleteCommand), nameof(ValidateCommand))]
    private CredentialRow? _selected;

    [ObservableProperty] private string _newDisplayName = string.Empty;
    [ObservableProperty] private CredentialType _newCredentialType = CredentialType.ApiKey;
    [ObservableProperty] private string _newSecretReference = string.Empty;
    [ObservableProperty] private string _newExpiration = string.Empty;
    [ObservableProperty] private string _newCustomerId = string.Empty;
    [ObservableProperty] private string _newTenantId = string.Empty;
    [ObservableProperty] private string _replacementExpiration = string.Empty;

    /// <summary>Transient secret typed for Add; cleared after the operation.</summary>
    public string NewSecretInput { private get; set; } = string.Empty;

    /// <summary>Transient secret typed for Replace; cleared after the operation.</summary>
    public string ReplacementSecretInput { private get; set; } = string.Empty;

    public OrganizationKeyPanel OrganizationKey { get; } = organizationKey;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await OrganizationKey.RefreshAsync();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var selectedId = Selected?.CredentialId;
        Credentials.Clear();
        foreach (var view in await credentials.ListAsync()) Credentials.Add(CredentialRow.From(view));
        Selected = Credentials.FirstOrDefault(row => row.CredentialId == selectedId);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        try
        {
            if (!TryParseDate(NewExpiration, out var expiration)) return;
            SecretReference? reference = null;
            if (!string.IsNullOrWhiteSpace(NewSecretReference) && !SecretReference.TryParse(NewSecretReference.Trim(), out reference))
            {
                StatusMessage = "The secret reference must use the form provider:name, for example os:customer/contoso/connectwise-api.";
                return;
            }

            var result = await credentials.AddAsync(new NewCredential(NewDisplayName, NewCredentialType, reference, expiration,
                Blank(NewCustomerId), Blank(NewTenantId)), NewSecretInput);
            StatusMessage = result.Succeeded ? $"Credential '{result.Value!.DisplayName}' stored as {result.Value.SecretReference}." : result.Error!.ToString();
            if (result.Succeeded)
            {
                NewDisplayName = NewSecretReference = NewExpiration = NewCustomerId = NewTenantId = string.Empty;
                await RefreshAsync();
            }
        }
        finally
        {
            ClearSecrets();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ReplaceAsync()
    {
        try
        {
            if (Selected is null || !TryParseDate(ReplacementExpiration, out var expiration)) return;
            var result = await credentials.ReplaceAsync(Selected.CredentialId, ReplacementSecretInput, expiration);
            StatusMessage = result.Succeeded ? $"Secret of '{Selected.DisplayName}' replaced." : result.Error!.ToString();
            if (result.Succeeded)
            {
                ReplacementExpiration = string.Empty;
                await RefreshAsync();
            }
        }
        finally
        {
            ClearSecrets();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var confirmed = dialogs.Confirm(new ConfirmationRequest("Delete Credential",
            "The secret will be deleted from the secret store and its metadata removed. Anything that uses this reference will stop authenticating until a new secret is stored.",
            [new("Credential", Selected.DisplayName), new("Type", Selected.CredentialType.ToString()), new("Reference", Selected.SecretReference)],
            "Delete credential", IsDestructive: true));
        if (!confirmed) return;
        var result = await credentials.DeleteAsync(Selected.CredentialId);
        StatusMessage = result.Succeeded ? $"Credential '{Selected.DisplayName}' deleted." : result.Error!.ToString();
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ValidateAsync()
    {
        if (Selected is null) return;
        var view = await credentials.ValidateAsync(Selected.CredentialId);
        StatusMessage = view is null ? "The credential no longer exists." : $"{view.Metadata.DisplayName}: {view.Status} - {view.StatusMessage}";
        await RefreshAsync();
    }

    private bool HasSelection() => Selected is not null;

    private bool TryParseDate(string text, out DateTimeOffset? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (DateTimeOffset.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            date = parsed.ToUniversalTime();
            return true;
        }

        StatusMessage = "Enter the expiration date as yyyy-MM-dd, or leave it empty.";
        return false;
    }

    private void ClearSecrets()
    {
        NewSecretInput = string.Empty;
        ReplacementSecretInput = string.Empty;
        SecretInputsCleared?.Invoke(this, EventArgs.Empty);
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
