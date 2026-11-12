using System.Windows;

namespace EngineeringWorkspace.Desktop.Pages.Tenants;

/// <summary>View-specific code only: hands the client secret to the ViewModel transiently and clears the box afterwards.</summary>
public partial class TenantAppRegistrationView
{
    public TenantAppRegistrationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TenantAppRegistrationViewModel previous) previous.SecretInputsCleared -= OnSecretInputsCleared;
        if (e.NewValue is TenantAppRegistrationViewModel current) current.SecretInputsCleared += OnSecretInputsCleared;
    }

    private void OnSecretInputsCleared(object? sender, EventArgs e) => ClientSecretBox.Clear();

    private void OnClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is TenantAppRegistrationViewModel viewModel) viewModel.ClientSecretInput = ClientSecretBox.Password;
    }
}
