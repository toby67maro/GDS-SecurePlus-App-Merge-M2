using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Desktop.Pages.Catalog;
using EngineeringWorkspace.Desktop.Services;

namespace EngineeringWorkspace.Desktop.Pages.Planning;

/// <summary>Services the Task Detail pane needs to remediate (registered in the Desktop composition root).</summary>
public sealed class TaskDetailServices(ChangeExecutionService executor, TaskRemediationService remediation, IChangeRequestRepository changeRequests, PathSettings paths)
{
    public ChangeExecutionService Executor { get; } = executor;
    public TaskRemediationService Remediation { get; } = remediation;
    public IChangeRequestRepository ChangeRequests { get; } = changeRequests;
    public string WindowStateFile { get; } = Path.Combine(paths.StateDirectory, "task-windows.json");
}

public sealed record TaskHistoryRow(ProjectTaskEvent Event)
{
    public string When => Event.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    public string Who => Event.CreatedBy;
    public string What => Event.EventType;
    public string Summary => Event.Summary + (Event.AttachmentName is null ? string.Empty : $" [attachment: {Event.AttachmentName}]");
}

/// <summary>
/// Task Detail (4.0, C2a/f/g): the task's fields, linked change requests and controls, history, the controls' remediation options (the same
/// options the Catalog shows), Remediate (guardrails: approved change request, preview with before state, confirmation with passcode, pilot
/// first, everything logged) and attested manual steps. Shown in the page's pane and, when popped out, in its own window – the same object, so
/// edits sync both ways immediately.
/// </summary>
public sealed partial class TaskDetailViewModel(ProjectPlanViewModel owner, ProjectTaskRow row) : ObservableObject
{
    public ProjectTaskRow Row { get; } = row;
    public ObservableCollection<TaskHistoryRow> History { get; } = [];
    public ObservableCollection<RemediationTab> RemediationTabs { get; } = [];
    public IReadOnlyList<string> Statuses => ProjectPlanViewModel.Statuses;

    [ObservableProperty] private RemediationTab? _selectedRemediation;
    [ObservableProperty] private string _gateText = string.Empty;
    [ObservableProperty] private bool _canRemediate;
    [ObservableProperty] private string _attestNote = string.Empty;
    [ObservableProperty] private string? _attachmentPath;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPoppedOut;

    public string Title => $"{Row.Outline}. {Row.Name}";

    public async Task LoadAsync()
    {
        OnPropertyChanged(nameof(Title));
        History.Clear();
        if (owner.PlanId is { } planId && owner.Planning.IsAvailable)
        {
            try
            {
                foreach (var entry in await owner.Planning.GetTaskEventsAsync(planId, Row.Key)) History.Add(new TaskHistoryRow(entry));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
            {
                Message = $"History could not be read: {exception.Message}";
            }
        }

        RemediationTabs.Clear();
        foreach (var id in Row.ControlIds)
        {
            if (owner.Catalog.Catalog.Controls.FirstOrDefault(control => string.Equals(control.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } control) continue;
            foreach (var tab in RemediationOptions.FromControl(control)) RemediationTabs.Add(tab with { Title = $"{control.Id}: {tab.Title}" });
            if (owner.Planning.IsAvailable && RemediationOptions.KnowledgeKeys(control) is { Count: > 0 } keys)
            {
                try
                {
                    foreach (var tab in RemediationOptions.FromKnowledge(control, await owner.Planning.FindSettingsAsync(keys)))
                        RemediationTabs.Add(tab with { Title = $"{control.Id}: {tab.Title}" });
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
                {
                    // Knowledge-base options are optional.
                }
            }
        }

        SelectedRemediation = RemediationTabs.FirstOrDefault();
        await EvaluateAsync();
    }

    private async Task<RemediationGate> EvaluateAsync()
    {
        RemediationGate gate;
        if (owner.DetailServices is not { } services || owner.SelectedCustomer is not { } customer || !services.ChangeRequests.IsAvailable)
            gate = new RemediationGate(false, "Remediation needs the application database and a customer.", null);
        else
        {
            try
            {
                gate = TaskRemediationService.Evaluate(Row.ToTask([]), await services.ChangeRequests.ListChangeRequestsAsync(customer.Id.Value));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
            {
                gate = new RemediationGate(false, exception.Message, null);
            }
        }

        CanRemediate = gate.Allowed;
        GateText = gate.Allowed ? $"{gate.Reason} Remediate creates its policies in the tenant (pilot first)." : gate.Reason;
        return gate;
    }

    [RelayCommand]
    private async Task RemediateAsync()
    {
        if (owner.DetailServices is not { } services || owner.SelectedCustomer is not { } customer || IsBusy) return;
        var gate = await EvaluateAsync();
        if (!gate.Allowed || gate.ChangeRequest is not { } request)
        {
            Message = gate.Reason;
            return;
        }

        IsBusy = true;
        try
        {
            if (!await owner.EnsureSavedAsync() || owner.PlanId is not { } planId)
            {
                Message = "Save the plan first.";
                return;
            }

            var scoped = TaskRemediationService.Scope(Row.ToTask([]), request);
            IReadOnlyList<ApplyPreviewItem> preview;
            try
            {
                preview = await Task.Run(() => services.Executor.PreviewApplyAsync(scoped, CancellationToken.None));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Net.Http.HttpRequestException)
            {
                Message = $"The tenant could not be checked: {exception.Message}";
                return;
            }

            var tenant = customer.PrimaryTenant;
            var details = new List<KeyValuePair<string, string>>
            {
                new("Tenant", $"{tenant?.DisplayName} ({tenant?.PrimaryDomain ?? tenant?.TenantId?.ToString() ?? "not connected"})"),
                new("Change request", $"#{request.ChangeRequestId} {request.Title} (approved by {request.ApprovedBy ?? "—"})"),
                new("Method", "Microsoft Graph; Conditional Access in report-only state, Intune policies unassigned (pilot first)"),
            };
            details.AddRange(preview.Select(item => new KeyValuePair<string, string>(item.Item, $"Before: {item.Before}. After: {item.After}{(item.PolicyName is null ? string.Empty : $" ('{item.PolicyName}')")}.")));
            details.Add(new("Rollback", request.RollbackPlan ?? "Delete the created policy in the tenant; its ID is recorded in the task history."));
            if (!owner.Dialogs.Confirm(new ConfirmationRequest("Remediate in the Live Tenant",
                    $"Secure Plus will create the policies below in {customer.Name}'s tenant now. Existing policies are skipped, nothing is changed or deleted, and every attempt is recorded on this task, the change request and the audit log.",
                    details, "Remediate", IsDestructive: true, Passcode: Application.Navigation.AdvancedModeService.IsPasscode,
                    PasscodeLabel: "Enter the passcode to allow this change")))
                return;

            var outcome = await services.Remediation.RemediateAsync(planId, Row.ToTask([]), request, preview,
                record => Task.Run(() => services.Executor.ApplyToTenantAsync(record, null, CancellationToken.None)));
            Row.Status = outcome.NewStatus;
            owner.Recalculate();
            owner.IsDirty = true;
            await owner.EnsureSavedAsync();
            Message = outcome.Created > 0
                ? $"{outcome.Summary} When the change is enforced (Conditional Access switched on / policy assigned) and the tenant is re-assessed, use the check mark to complete the task."
                : outcome.Summary;
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseAttachment() => AttachmentPath = owner.Dialogs.OpenFile("Evidence (*.png;*.jpg;*.pdf;*.txt;*.log;*.zip;*.xml;*.html)|*.png;*.jpg;*.jpeg;*.pdf;*.txt;*.log;*.zip;*.xml;*.html|All files (*.*)|*.*");

    /// <summary>Records a manual (GPO, registry, PowerShell) step as "Manual – attested": a note is required, the attachment is optional (10 MB).</summary>
    [RelayCommand]
    private async Task AttestAsync()
    {
        if (owner.DetailServices is not { } services || IsBusy) return;
        IsBusy = true;
        try
        {
            if (!await owner.EnsureSavedAsync() || owner.PlanId is not { } planId)
            {
                Message = "Save the plan first.";
                return;
            }

            byte[]? file = null;
            if (!string.IsNullOrWhiteSpace(AttachmentPath))
            {
                if (new FileInfo(AttachmentPath).Length > TaskRemediationService.MaxAttachmentBytes)
                {
                    Message = "The attachment is larger than 10 MB.";
                    return;
                }

                file = await File.ReadAllBytesAsync(AttachmentPath);
            }

            await services.Remediation.AttestAsync(planId, Row.ToTask([]), AttestNote, AttachmentPath is null ? null : Path.GetFileName(AttachmentPath), file);
            AttestNote = string.Empty;
            AttachmentPath = null;
            Message = "Recorded as Manual – attested (not a verified change). Re-assess to verify it.";
            await LoadAsync();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            Message = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void PopOut() => TaskWindows.Show(this, owner.DetailServices?.WindowStateFile);

    [RelayCommand]
    private void DockBack() => TaskWindows.Close(this);
}

/// <summary>Pop-out task windows: one per task; size and position remembered; docked back when its monitor is gone.</summary>
public static class TaskWindows
{
    private static readonly Dictionary<string, TaskDetailWindow> Open = new(StringComparer.Ordinal);
    private sealed record Placement(double Left, double Top, double Width, double Height);

    public static void Show(TaskDetailViewModel detail, string? stateFile)
    {
        if (Open.TryGetValue(detail.Row.Key, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new TaskDetailWindow { DataContext = detail, Owner = System.Windows.Application.Current?.MainWindow };
        var placements = Load(stateFile);
        if (placements.TryGetValue(detail.Row.Key, out var saved))
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!screen.IntersectsWith(new Rect(saved.Left, saved.Top, Math.Max(100, saved.Width), Math.Max(100, saved.Height))))
            {
                // The monitor it was on is gone: stay docked.
                detail.Message = "The window's last monitor is not connected, so the task stays docked here.";
                placements.Remove(detail.Row.Key);
                Save(stateFile, placements);
                return;
            }

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            (window.Left, window.Top, window.Width, window.Height) = (saved.Left, saved.Top, saved.Width, saved.Height);
        }

        window.Closed += (_, _) =>
        {
            Open.Remove(detail.Row.Key);
            detail.IsPoppedOut = false;
            var current = Load(stateFile);
            current[detail.Row.Key] = new Placement(window.RestoreBounds.Left, window.RestoreBounds.Top, window.RestoreBounds.Width, window.RestoreBounds.Height);
            Save(stateFile, current);
        };
        Open[detail.Row.Key] = window;
        detail.IsPoppedOut = true;
        window.Show();
    }

    public static void Close(TaskDetailViewModel detail)
    {
        if (Open.TryGetValue(detail.Row.Key, out var window)) window.Close();
    }

    private static Dictionary<string, Placement> Load(string? file)
    {
        try
        {
            return file is not null && File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, Placement>>(File.ReadAllText(file)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Save(string? file, Dictionary<string, Placement> placements)
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(placements));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Placement is a convenience only.
        }
    }
}
