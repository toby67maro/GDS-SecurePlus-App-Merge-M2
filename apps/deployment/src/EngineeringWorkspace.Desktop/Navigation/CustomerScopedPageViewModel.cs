using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Navigation;

/// <summary>
/// Base of every page with its own Customer drop-down. The page starts on the customer selected at the top of the
/// application and follows it when it changes, until the user picks a different customer on the page; picking the
/// global customer again resumes following.
/// </summary>
public abstract partial class CustomerScopedPageViewModel(ICustomerContextService customerContext, ICustomerProfileStore customerStore) : PageViewModel
{
    private bool _followsGlobal = true;
    private bool _settingSelection;

    protected ICustomerContextService CustomerContext { get; } = customerContext;
    protected ICustomerProfileStore CustomerStore { get; } = customerStore;

    public ObservableCollection<CustomerProfile> CustomerOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomer), nameof(FollowsGlobalText))]
    private CustomerProfile? _selectedCustomer;

    public bool HasCustomer => SelectedCustomer is not null;

    /// <summary>Shown under the drop-down: whether the page follows the customer at the top of the application.</summary>
    public string FollowsGlobalText => _followsGlobal ? "Follows the customer selected at the top of the application." : "Showing a different customer from the one at the top.";

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        CustomerContext.CustomerChanged += OnGlobalCustomerChanged;
        await LoadCustomerOptionsAsync(cancellationToken);
        await SelectAsync(CustomerContext.CurrentCustomer?.Id, fromGlobal: true);
    }

    public override void OnNavigatedFrom()
    {
        CustomerContext.CustomerChanged -= OnGlobalCustomerChanged;
        base.OnNavigatedFrom();
    }

    /// <summary>Called when the page's customer changes (initially, by the user or by following the global customer).</summary>
    protected abstract Task OnCustomerChangedAsync(CustomerProfile? customer);

    protected async Task LoadCustomerOptionsAsync(CancellationToken cancellationToken)
    {
        var profiles = await CustomerStore.ListAsync(cancellationToken);
        CustomerOptions.Clear();
        foreach (var profile in profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)) CustomerOptions.Add(profile);
    }

    partial void OnSelectedCustomerChanged(CustomerProfile? value)
    {
        if (!_settingSelection) _followsGlobal = value?.Id == CustomerContext.CurrentCustomer?.Id;
        OnPropertyChanged(nameof(FollowsGlobalText));
        _ = NotifyCustomerChangedAsync(value);
    }

    private async Task NotifyCustomerChangedAsync(CustomerProfile? value)
    {
        try
        {
            await OnCustomerChangedAsync(value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusMessage = $"The data for {value?.Name ?? "the customer"} could not be loaded: {exception.Message}";
        }
    }

    private void OnGlobalCustomerChanged(object? sender, CustomerContextChanged e)
    {
        if (!_followsGlobal) return;
        Shell.ShellViewModel.OnUi(() => _ = SelectAsync(e.Current?.Id, fromGlobal: true));
    }

    private async Task SelectAsync(Domain.Common.CustomerId? id, bool fromGlobal)
    {
        if (id is { } value && CustomerOptions.All(option => option.Id != value)) await LoadCustomerOptionsAsync(CancellationToken.None);
        _settingSelection = fromGlobal;
        try
        {
            SelectedCustomer = id is null ? null : CustomerOptions.FirstOrDefault(option => option.Id == id);
            if (fromGlobal) _followsGlobal = true;
        }
        finally
        {
            _settingSelection = false;
        }
    }
}
