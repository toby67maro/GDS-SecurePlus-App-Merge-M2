using System.Windows;

namespace EngineeringWorkspace.Desktop.Pages.Credentials;

/// <summary>View-specific code only: hands PasswordBox values to the ViewModel transiently and clears them afterwards.</summary>
public partial class CredentialManagementView
{
    public CredentialManagementView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CredentialManagementViewModel previous) previous.SecretInputsCleared -= OnSecretInputsCleared;
        if (e.NewValue is CredentialManagementViewModel current) current.SecretInputsCleared += OnSecretInputsCleared;
    }

    private void OnSecretInputsCleared(object? sender, EventArgs e)
    {
        NewSecretBox.Clear();
        ReplacementSecretBox.Clear();
    }

    private void OnNewSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CredentialManagementViewModel viewModel) viewModel.NewSecretInput = NewSecretBox.Password;
    }

    private void OnReplacementSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CredentialManagementViewModel viewModel) viewModel.ReplacementSecretInput = ReplacementSecretBox.Password;
    }
}
