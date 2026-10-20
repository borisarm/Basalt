using System.Globalization;
using Basalt.Presentation.Common;
using Basalt.Presentation.Localization;

namespace Basalt.Testing.Fakes;

/// <summary>Runs everything inline on the calling thread.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }

    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }
}

/// <summary>Records notifications.</summary>
public sealed class FakeNotificationService : INotificationService
{
    public List<Notification> Shown { get; } = [];

    public void Show(Notification notification) => Shown.Add(notification);
}

/// <summary>Answers dialogs as scripted and records the requests.</summary>
public sealed class FakeDialogService : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;

    public string? PromptAnswer { get; set; }

    public string? FolderAnswer { get; set; }

    public List<object> Requests { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(PromptRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(PromptAnswer);
    }

    public Task<string?> PickFolderAsync(string title, CancellationToken ct)
    {
        Requests.Add(title);
        return Task.FromResult(FolderAnswer);
    }
}

public static class PresentationFakes
{
    /// <summary>The real resource-backed localizer in English (tests assert on actual texts).</summary>
    public static ILocalizer Localizer(string culture = "en") => new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
}
