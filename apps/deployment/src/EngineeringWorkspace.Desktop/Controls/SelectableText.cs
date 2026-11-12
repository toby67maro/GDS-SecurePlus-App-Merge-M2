using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>
/// Makes text anywhere in the application selectable and copyable: a TextBlock can be highlighted with the mouse and
/// copied with Ctrl+C or its context menu (Copy / Copy All). WPF only offers selection on TextBox and RichTextBox, so
/// this attaches WPF's own (internal) read-only text editor to each TextBlock. TextBlocks inside buttons, list items,
/// menus, headers and other clickable elements are left alone so clicks keep working. If the internal editor is not
/// available, every TextBlock still gets the Copy / Copy All menu.
/// </summary>
public static class SelectableText
{
    private static readonly Type? EditorType = typeof(TextBox).Assembly.GetType("System.Windows.Documents.TextEditor");
    private static readonly Type? ContainerType = typeof(TextBox).Assembly.GetType("System.Windows.Documents.ITextContainer");
    private static readonly PropertyInfo? ContainerProperty = typeof(TextBlock).GetProperty("TextContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? ContainerViewProperty = ContainerType?.GetProperty("TextView");
    private static readonly PropertyInfo? EditorReadOnly = EditorType?.GetProperty("IsReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? EditorView = EditorType?.GetProperty("TextView", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? EditorSelection = EditorType?.GetProperty("Selection", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly DependencyProperty EditorKey =
        DependencyProperty.RegisterAttached("Editor", typeof(object), typeof(SelectableText), new PropertyMetadata(null));

    private static bool _editorAvailable;

    /// <summary>Called once at startup.</summary>
    public static void Install()
    {
        try
        {
            var register = EditorType?.GetMethod("RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic, null,
                [typeof(Type), typeof(bool), typeof(bool), typeof(bool)], null);
            if (register is not null && ContainerProperty is not null && ContainerViewProperty is not null && EditorReadOnly is not null && EditorView is not null)
            {
                register.Invoke(null, [typeof(TextBlock), true, true, true]);
                _editorAvailable = true;
            }
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or MemberAccessException)
        {
            _editorAvailable = false;
        }

        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock block || block.GetValue(EditorKey) is not null || !IsEligible(block)) return;
        block.SetValue(EditorKey, string.Empty);
        if (_editorAvailable) Attach(block);
        block.ContextMenu ??= CreateMenu(block);
    }

    private static void Attach(TextBlock block)
    {
        try
        {
            var container = ContainerProperty!.GetValue(block);
            var editor = Activator.CreateInstance(EditorType!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.CreateInstance, null,
                [container, block, false], null);
            EditorReadOnly!.SetValue(editor, true);
            EditorView!.SetValue(editor, ContainerViewProperty!.GetValue(container));
            block.SetValue(EditorKey, editor);
            block.Focusable = true;
            block.FocusVisualStyle = null;
            KeyboardNavigation.SetIsTabStop(block, false);
            block.Cursor = Cursors.IBeam;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or MemberAccessException or MissingMethodException)
        {
            _editorAvailable = false;
        }
    }

    private static ContextMenu CreateMenu(TextBlock block)
    {
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => Copy(Selected(block) is { Length: > 0 } selected ? selected : AllText(block));
        var copyAll = new MenuItem { Header = "Copy All" };
        copyAll.Click += (_, _) => Copy(AllText(block));
        return new ContextMenu { Items = { copy, copyAll } };
    }

    private static string? Selected(TextBlock block)
    {
        var editor = block.GetValue(EditorKey);
        if (editor is null or string || EditorSelection is null) return null;
        return (EditorSelection.GetValue(editor) as TextRange)?.Text;
    }

    private static string AllText(TextBlock block) =>
        block.Inlines.Count > 0 ? new TextRange(block.ContentStart, block.ContentEnd).Text : block.Text;

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy (another application holds it); the user can try again.
        }
    }

    /// <summary>False inside clickable elements, where selecting text would swallow the click.</summary>
    private static bool IsEligible(TextBlock block)
    {
        if (!block.IsHitTestVisible) return false;
        DependencyObject? current = block;
        while ((current = Parent(current!)) is not null)
        {
            switch (current)
            {
                case ButtonBase or ListBoxItem or ComboBox or ComboBoxItem or MenuItem or TreeViewItem or DataGridCell or DataGridColumnHeader or Thumb or ScrollBar
                    or TabItem or GridViewColumnHeader or ToolTip or ContextMenu or TextBoxBase or PasswordBox:
                    return false;
                case FrameworkElement element when element.Cursor == Cursors.Hand || element.InputBindings.Count > 0:
                    return false;
            }
        }

        return true;
    }

    private static DependencyObject? Parent(DependencyObject child) =>
        child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
}
