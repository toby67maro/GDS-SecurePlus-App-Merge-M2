using System.Windows;

namespace EngineeringWorkspace.Desktop.Pages.Planning;

/// <summary>A popped-out task (one per task). Its DataContext is the page's TaskDetailViewModel, so edits sync both ways.</summary>
public partial class TaskDetailWindow : Window
{
    public TaskDetailWindow() => InitializeComponent();
}
