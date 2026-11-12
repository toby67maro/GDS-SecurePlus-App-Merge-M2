using CommunityToolkit.Mvvm.ComponentModel;
using EngineeringWorkspace.Application.Navigation;

namespace EngineeringWorkspace.Desktop.Navigation;

/// <summary>Base of every page ViewModel. Pages are resolved from DI per navigation (transient).</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Called after navigation with the destination's route descriptor.</summary>
    public virtual Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        Title = route.DisplayName;
        Description = route.Description;
        return Task.CompletedTask;
    }

    /// <summary>Called when the page is left; cancel in-flight work here.</summary>
    public virtual void OnNavigatedFrom()
    {
    }
}
