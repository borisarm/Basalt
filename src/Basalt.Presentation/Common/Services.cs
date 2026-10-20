using Basalt.Domain.Notes;
using CommunityToolkit.Mvvm.Messaging;

namespace Basalt.Presentation.Common;

/// <summary>Runs work on the UI thread (Avalonia in the UI layer, inline in tests).</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action);

    Task InvokeAsync(Func<Task> action);
}

public enum NotificationSeverity
{
    Information,
    Success,
    Warning,
    Error,
}

/// <summary>A toast/banner; <see cref="Action"/> adds a button (e.g. "Retry", "Open conflict").</summary>
public sealed record Notification(NotificationSeverity Severity, string Message, string? ActionText = null, Func<Task>? Action = null);

public interface INotificationService
{
    void Show(Notification notification);
}

/// <summary>A yes/no question; destructive confirmations are styled as such.</summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText, string? CancelText = null, bool IsDestructive = false);

public sealed record PromptRequest(string Title, string Label, string? InitialValue = null);

/// <summary>A question with several answers (plus cancel).</summary>
public sealed record ChoiceRequest(string Title, string Message, IReadOnlyList<string> Options);

/// <summary>Modal dialogs. Texts arrive already localized.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct);

    /// <returns>The text entered, or null when cancelled.</returns>
    Task<string?> PromptAsync(PromptRequest request, CancellationToken ct);

    /// <returns>The index of the chosen option, or null when cancelled.</returns>
    Task<int?> ChooseAsync(ChoiceRequest request, CancellationToken ct);

    /// <returns>The chosen folder, or null when cancelled (desktop).</returns>
    Task<string?> PickFolderAsync(string title, CancellationToken ct);
}

/// <summary>Where and how to open a file.</summary>
public sealed record OpenRequest(VaultPath Path, bool NewTab = false, string? Subpath = null, int? Line = null);

/// <summary>Navigation inside the open vault (spec 07 §2), implemented by <c>WorkspaceViewModel</c>.</summary>
public interface IWorkspaceNavigator
{
    Task OpenAsync(OpenRequest request, CancellationToken ct);

    void FocusPanel(string panelId);
}

/// <summary>UI messaging between sibling ViewModels of one workspace (spec 07 §2).</summary>
public interface IWorkspaceMessenger
{
    void Send<TMessage>(TMessage message)
        where TMessage : class;

    void Register<TMessage>(object recipient, Action<object, TMessage> handler)
        where TMessage : class;

    void UnregisterAll(object recipient);
}

/// <summary><see cref="IWorkspaceMessenger"/> over a private <see cref="WeakReferenceMessenger"/> (recipients are not kept alive).</summary>
public sealed class WorkspaceMessenger : IWorkspaceMessenger
{
    private readonly WeakReferenceMessenger _messenger = new();

    public void Send<TMessage>(TMessage message)
        where TMessage : class => _messenger.Send(message);

    public void Register<TMessage>(object recipient, Action<object, TMessage> handler)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        _messenger.Register<object, TMessage>(recipient, (r, m) => handler(r, m));
    }

    public void UnregisterAll(object recipient) => _messenger.UnregisterAll(recipient);
}
