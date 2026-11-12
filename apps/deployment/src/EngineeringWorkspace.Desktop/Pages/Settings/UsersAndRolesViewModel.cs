using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;

namespace EngineeringWorkspace.Desktop.Pages.Settings;

public sealed record UserRow(AppUser User)
{
    public string Account => User.WindowsAccount;
    public string Name => User.DisplayName ?? "—";
    public string Upn => User.Upn ?? "—";
    public string Role => AppRoles.DisplayName(User.RoleCode);
    public string Status => User.IsActive ? "Active" : "Inactive";
    public string LastSeen => User.LastSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
}

/// <summary>
/// Settings > Users &amp; Roles (BLUEPRINT §9): a Lead assigns each person a role by Windows account (GDS\name) and sign-in name,
/// and grants their SQL Server login the matching database role. No AD groups. Until the first user is added everyone has full
/// access, as in 3.1; the first user must be a Lead.
/// </summary>
public sealed partial class UsersAndRolesViewModel(IUserDirectory users) : PageViewModel
{
    public ObservableCollection<UserRow> Rows { get; } = [];
    public IReadOnlyList<Choice<string>> Roles { get; } = AppRoles.All.Select(role => new Choice<string>(role, AppRoles.DisplayName(role))).ToArray();

    public string CurrentAccount => users.CurrentAccount;

    [ObservableProperty] private string _yourAccess = string.Empty;
    [ObservableProperty] private bool _isEnforced;
    [ObservableProperty] private bool _canManage;
    [ObservableProperty] private UserRow? _selectedRow;
    [ObservableProperty] private string _windowsAccount = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _upn = string.Empty;
    [ObservableProperty] private Choice<string>? _role;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasScript))] private string _script = string.Empty;

    public bool HasScript => !string.IsNullOrWhiteSpace(Script);

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await LoadAsync();
    }

    partial void OnSelectedRowChanged(UserRow? value)
    {
        if (value is null) return;
        WindowsAccount = value.User.WindowsAccount;
        DisplayName = value.User.DisplayName ?? string.Empty;
        Upn = value.User.Upn ?? string.Empty;
        Role = Roles.FirstOrDefault(choice => choice.Value == value.User.RoleCode);
        IsActive = value.User.IsActive;
        Script = string.Empty;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            Rows.Clear();
            foreach (var user in await users.ListAsync()) Rows.Add(new UserRow(user));
            IsEnforced = Rows.Any(row => row.User.IsActive);
            var me = await users.CurrentAsync();
            CanManage = !IsEnforced || AppRoles.CanManageUsers(me?.RoleCode);
            YourAccess = !IsEnforced
                ? $"No users are defined yet, so everyone has full access (as in 3.1). Add yourself first as a Lead ({CurrentAccount})."
                : me is null
                    ? $"{CurrentAccount} is not a Secure Plus user. Ask a Lead to add you."
                    : $"You are signed in to Windows as {CurrentAccount}: {AppRoles.DisplayName(me.RoleCode)}.";
            if (!IsEnforced && string.IsNullOrEmpty(WindowsAccount))
            {
                WindowsAccount = CurrentAccount;
                Role = Roles.First(choice => choice.Value == AppRoles.Lead);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = $"Users could not be loaded: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void New()
    {
        SelectedRow = null;
        WindowsAccount = string.Empty;
        DisplayName = string.Empty;
        Upn = string.Empty;
        Role = Roles.First(choice => choice.Value == AppRoles.Engineer);
        IsActive = true;
        Script = string.Empty;
    }

    private AppUser? Current()
    {
        if (Role is null)
        {
            StatusMessage = "Choose a role.";
            return null;
        }

        return new AppUser(AppUser.NormalizeAccount(WindowsAccount), Upn, DisplayName, Role.Value, IsActive);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Current() is not { } user) return;
        try
        {
            IsBusy = true;
            await users.SaveAsync(user);
            StatusMessage = $"{user.WindowsAccount} saved as {AppRoles.DisplayName(user.RoleCode)}{(user.IsActive ? string.Empty : " (inactive)")}. Use Grant Database Access so their SQL login matches.";
            await LoadAsync();
            SelectedRow = Rows.FirstOrDefault(row => string.Equals(row.Account, user.WindowsAccount, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GrantAsync()
    {
        if (Current() is not { } user) return;
        if (!AppUser.IsValidAccount(user.WindowsAccount))
        {
            StatusMessage = "Enter the Windows account as DOMAIN\\name, for example GDS\\jsmith.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await users.ApplyDatabaseAccessAsync(user);
            StatusMessage = result.Message;
            Script = result.Applied ? string.Empty : result.Script;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowScript()
    {
        if (Current() is not { } user) return;
        if (!AppUser.IsValidAccount(user.WindowsAccount))
        {
            StatusMessage = "Enter the Windows account as DOMAIN\\name, for example GDS\\jsmith.";
            return;
        }

        Script = users.DatabaseAccessScript(user);
        StatusMessage = "A SQL Server administrator (securityadmin) can run this script to grant the database access.";
    }

    [RelayCommand]
    private void CopyScript()
    {
        if (!HasScript) return;
        Clipboard.SetText(Script);
        StatusMessage = "Script copied.";
    }
}
