using System.Windows;
using System.Windows.Controls;

namespace EngineeringWorkspace.Desktop.Pages.Assessments;

public partial class AssessmentHistoryView : UserControl
{
    public AssessmentHistoryView() => InitializeComponent();

    /// <summary>"Day of Month" is the inverse of "Nth Weekday"; selecting it clears the Nth weekday mode.</summary>
    private void OnDayOfMonthChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ScheduleEditorViewModel editor) editor.IsNthWeekdayMode = false;
    }
}
