using System.Windows;

namespace EngineeringWorkspace.Desktop.Pages.Database;

/// <summary>
/// View-specific code only: PasswordBox cannot bind, so typed secrets are handed to the ViewModel transiently and the
/// boxes are cleared when the ViewModel reports the secret was stored.
/// </summary>
public partial class DatabaseConfigurationView
{
    public DatabaseConfigurationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DatabaseConfigurationViewModel previous) previous.SecretInputsCleared -= OnSecretInputsCleared;
        if (e.NewValue is DatabaseConfigurationViewModel current) current.SecretInputsCleared += OnSecretInputsCleared;
    }

    private void OnSecretInputsCleared(object? sender, EventArgs e)
    {
        SqlPasswordBox.Clear();
        ServicePrincipalSecretBox.Clear();
    }

    private void OnSqlPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DatabaseConfigurationViewModel viewModel) viewModel.SqlPasswordInput = SqlPasswordBox.Password;
    }

    private void OnServicePrincipalSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DatabaseConfigurationViewModel viewModel) viewModel.ServicePrincipalSecretInput = ServicePrincipalSecretBox.Password;
    }
}
