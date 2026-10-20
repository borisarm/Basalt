using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Tags;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Text;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Workspace;

/// <summary>
/// The editor view as seen by its tab (spec 07 §5.1): the tab is told that the text changed and asks for it only when
/// it needs it, so typing does not copy the document on every key.
/// </summary>
public interface IEditorBridge
{
    int Caret { get; }

    string GetText();

    void SetText(string text, int caret);
}

public sealed record NoteTabUseCases(IQueryHandler<ReadNote, NoteDocument> Read, ICommandHandler<SaveNote, BlobSha> Save);

/// <summary>The use cases behind editor completion and image paste (F-004.1–F-004.3, F-003.5).</summary>
public sealed record NoteEditingUseCases(
    IQueryHandler<SuggestLinkTargets, IReadOnlyList<LinkSuggestion>> Links,
    IQueryHandler<SuggestTags, IReadOnlyList<TagSuggestion>> Tags,
    ICommandHandler<EnsureBlockId, BlockId> EnsureBlockId,
    ICommandHandler<ImportAttachment, ImportedAttachment> Import);

/// <summary>Local edits and a disk version that do not merge cleanly (F-003.3).</summary>
public sealed record ExternalConflict(string Mine, string Disk, BlobSha? DiskSha);

/// <summary>Asks the workspace to show the differences of an <see cref="ExternalConflict"/>.</summary>
public sealed record ExternalConflictDiffRequested(VaultPath Path, string Mine, string Disk);

/// <summary>
/// A note open in a tab (F-003). Autosaves 2 s after the last change and on flush (close, navigation); writes use the
/// SHA of the last loaded/saved version so concurrent changes are never overwritten. External changes reload the note
/// when there are no local edits (F-003.2) or are merged three-way with them (F-003.3); a conflict keeps the local text
/// untouched and pauses autosave until the user keeps theirs, takes the disk version or looks at the differences.
/// </summary>
public sealed partial class NoteTabViewModel : TabViewModel
{
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(2);

    private readonly NoteTabUseCases _useCases;
    private readonly NoteEditingUseCases? _editing;
    private readonly IWorkspaceMessenger _messenger;
    private readonly VaultId _vault;
    private readonly IDisposable[] _subscriptions;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private IEditorBridge? _editor;
    private string _pendingText = string.Empty;
    private string _base = string.Empty;
    private BlobSha? _baseSha;
    private int _version;
    private ITimer? _autosave;
    private Task _lastSave = Task.CompletedTask;

    public NoteTabViewModel(
        ViewModelServices services,
        NoteTabUseCases useCases,
        IEventBus events,
        IWorkspaceMessenger messenger,
        VaultId vault,
        NoteEditingUseCases? editing = null,
        IEditorCommands? editorCommands = null)
        : base(services)
    {
        ArgumentNullException.ThrowIfNull(events);
        _useCases = useCases;
        _editing = editing;
        Editing = editorCommands ?? new EditorCommandsService();
        _messenger = messenger;
        _vault = vault;
        _subscriptions =
        [
            events.Subscribe<VaultFileChangedExternally>((e, _) => OnDiskChanged(e.Id, [e.Change.Path])),
            events.Subscribe<FilesChangedBySync>((e, _) => OnDiskChanged(e.Id, e.Paths)),
        ];
    }

    public override string Kind => "note";

    /// <summary>The pure editor behaviours the view applies on keys (spec 07 §5.2).</summary>
    public IEditorCommands Editing { get; }

    [ObservableProperty]
    public partial bool IsReadingMode { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepMineCommand), nameof(UseDiskCommand), nameof(ShowConflictDiffCommand))]
    public partial ExternalConflict? Conflict { get; private set; }

    private string CurrentText => _editor?.GetText() ?? _pendingText;

    /// <summary>Connects the editor view; it receives the current text.</summary>
    public void AttachEditor(IEditorBridge editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        editor.SetText(_pendingText, 0);
    }

    public void DetachEditor()
    {
        if (_editor is not null)
        {
            _pendingText = _editor.GetText();
            _editor = null;
        }
    }

    /// <summary>Called by the view on every change of the text.</summary>
    public void OnTextChanged()
    {
        _version++;
        IsDirty = true;
        RestartAutosave();
    }

    /// <summary>Called by the view when the caret moves to another line (the outline highlights its section).</summary>
    public void OnCaretLineChanged(int line)
    {
        if (Path is { } path)
        {
            _messenger.Send(new CaretLineChanged(path, line));
        }
    }

    /// <summary>Suggestions for the link or tag being typed at the caret (F-004.1–F-004.3).</summary>
    public async Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(TextBufferState state, CancellationToken ct)
    {
        if (_editing is null || Path is not { } path || CompletionTrigger.Detect(state.Text, state.SelectionStart) is not { } context)
        {
            return [];
        }

        if (context.Kind == CompletionKind.Link)
        {
            var links = await _editing.Links.HandleAsync(new SuggestLinkTargets(_vault, path, context.Partial), ct).ConfigureAwait(true);
            return links.IsFailure ? [] : [.. links.Value.Select(l => new CompletionItem(l.Display, l.Kind == LinkSuggestionKind.Note ? l.Path.ToString() : null, CompletionKind.Link, l))];
        }

        var tags = await _editing.Tags.HandleAsync(new SuggestTags(_vault, context.Partial), ct).ConfigureAwait(true);
        return tags.IsFailure ? [] : [.. tags.Value.Select(t => new CompletionItem(t.Name, t.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), CompletionKind.Tag, t))];
    }

    /// <summary>The edit that replaces the typed partial with the accepted suggestion; a block without id gets one first.</summary>
    public async Task<Result<Editor.TextEdit>> AcceptCompletionAsync(TextBufferState state, CompletionItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_editing is null || CompletionTrigger.Detect(state.Text, state.SelectionStart) is not { } context)
        {
            return Unavailable();
        }

        string insert;
        switch (item.Source)
        {
            case LinkSuggestion { Kind: LinkSuggestionKind.Block, Subpath: null, Line: { } line } block:
                var id = await RunAsync(token => _editing.EnsureBlockId.HandleAsync(new EnsureBlockId(_vault, block.Path, line), token)).ConfigureAwait(true);
                if (id.IsFailure)
                {
                    return id.Error;
                }

                insert = $"{block.Target}#^{id.Value}";
                break;
            case LinkSuggestion link:
                insert = link.Target + (link.Subpath is { } subpath ? "#" + subpath : string.Empty) + (link.Alias is { } alias ? "|" + alias : string.Empty);
                break;
            case TagSuggestion tag:
                insert = tag.Name;
                break;
            default:
                return Unavailable();
        }

        return new Editor.TextEdit(context.Start, state.SelectionStart - context.Start, insert);
    }

    /// <summary>Saves a pasted image as an attachment and returns the embed to insert at the caret (F-003.5).</summary>
    public async Task<Result<string>> PasteImageAsync(byte[] bytes, string extension, CancellationToken ct)
    {
        if (_editing is null || Path is not { } path)
        {
            return Unavailable();
        }

        var name = $"Pasted image {Services.Time.GetLocalNow():yyyyMMddHHmmss}.{extension}";
        var imported = await RunAsync(token => _editing.Import.HandleAsync(new ImportAttachment(_vault, path, name, bytes), token)).ConfigureAwait(true);
        return imported.Map(i => i.EmbedText);
    }

    private static Error Unavailable() => new(ErrorCodes.CommandUnavailable, "Not available in this tab.");

    /// <summary>Completes when the save in progress (if any) has finished.</summary>
    public Task WhenSavedAsync() => _lastSave;

    public override async Task FlushAsync(CancellationToken ct)
    {
        _autosave?.Dispose();
        _autosave = null;
        await SaveAsync().ConfigureAwait(true);
    }

    protected override async Task<Result<Unit>> LoadAsync(OpenRequest request, CancellationToken ct)
    {
        await FlushAsync(ct).ConfigureAwait(true);
        var note = await RunAsync(token => _useCases.Read.HandleAsync(new ReadNote(_vault, request.Path), token)).ConfigureAwait(true);
        if (note.IsFailure)
        {
            return note.Error;
        }

        Path = request.Path;
        Title = request.Path.NameWithoutExtension;
        Conflict = null;
        Accept(note.Value.Content.Text, note.Value.Sha);
        SetText(note.Value.Content.Text, 0);
        IsDirty = false;
        return Result.Success();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _autosave?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RestartAutosave()
    {
        _autosave?.Dispose();
        _autosave = Services.Time.CreateTimer(_ => Services.Dispatcher.Post(() => _lastSave = SaveAsync()), null, AutosaveDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (Path is not { } path || Conflict is not null || !IsDirty)
            {
                return;
            }

            var text = CurrentText;
            var version = _version;
            if (text == _base)
            {
                IsDirty = version != _version;
                return;
            }

            var saved = await RunAsync(token => _useCases.Save.HandleAsync(new SaveNote(_vault, path, text, _baseSha), token), notifyErrors: false).ConfigureAwait(true);
            if (saved.IsSuccess)
            {
                Accept(text, saved.Value);
                IsDirty = version != _version;
            }
            else if (saved.Error.Code == ErrorCodes.NoteModifiedExternally)
            {
                ApplyDiskVersion(saved.Error.Metadata?.GetValueOrDefault("currentText") as string, ParseSha(saved.Error.Metadata?.GetValueOrDefault("currentSha") as string));
            }
            else if (saved.Error.Code != ErrorCodes.OperationCancelled)
            {
                Services.Notifications.Show(new Notification(NotificationSeverity.Error, Localizer.Error(saved.Error)));
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async ValueTask OnDiskChanged(VaultId vault, IEnumerable<VaultPath> paths)
    {
        if (vault != _vault || Path is not { } path || !paths.Contains(path))
        {
            return;
        }

        await Services.Dispatcher.InvokeAsync(async () =>
        {
            var disk = await RunAsync(token => _useCases.Read.HandleAsync(new ReadNote(_vault, path), token), notifyErrors: false).ConfigureAwait(true);
            if (disk.IsSuccess && disk.Value.Sha != _baseSha)
            {
                ApplyDiskVersion(disk.Value.Content.Text, disk.Value.Sha);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Reloads (no local edits) or merges three-way with the last loaded/saved text as base.</summary>
    private void ApplyDiskVersion(string? disk, BlobSha? diskSha)
    {
        if (disk is null)
        {
            // Deleted meanwhile: keep the text; the next save recreates the note.
            _base = string.Empty;
            _baseSha = null;
            IsDirty = true;
            RestartAutosave();
            return;
        }

        var mine = CurrentText;
        if (!IsDirty)
        {
            SetText(disk, CaretMapper.Map(mine, disk, _editor?.Caret ?? 0));
            Accept(disk, diskSha);
            return;
        }

        var merged = ThreeWayTextMerger.Merge(_base, mine, disk);
        if (!merged.IsClean)
        {
            _autosave?.Dispose();
            _autosave = null;
            Conflict = new ExternalConflict(mine, disk, diskSha);
            return;
        }

        SetText(merged.Text, CaretMapper.Map(mine, merged.Text, _editor?.Caret ?? 0));
        Accept(disk, diskSha);
        IsDirty = merged.Text != disk;
        if (IsDirty)
        {
            RestartAutosave();
        }
    }

    private bool HasConflict() => Conflict is not null;

    [RelayCommand(CanExecute = nameof(HasConflict))]
    private async Task KeepMineAsync()
    {
        if (Conflict is not { } conflict)
        {
            return;
        }

        // Overwrite exactly the disk version the user saw; anything newer is detected again.
        Accept(conflict.Disk, conflict.DiskSha);
        Conflict = null;
        IsDirty = true;
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasConflict))]
    private void UseDisk()
    {
        if (Conflict is not { } conflict)
        {
            return;
        }

        SetText(conflict.Disk, CaretMapper.Map(CurrentText, conflict.Disk, _editor?.Caret ?? 0));
        Accept(conflict.Disk, conflict.DiskSha);
        Conflict = null;
        IsDirty = false;
    }

    [RelayCommand(CanExecute = nameof(HasConflict))]
    private void ShowConflictDiff()
    {
        if (Conflict is { } conflict && Path is { } path)
        {
            _messenger.Send(new ExternalConflictDiffRequested(path, conflict.Mine, conflict.Disk));
        }
    }

    [RelayCommand]
    private void ToggleReadingMode() => IsReadingMode = !IsReadingMode;

    private void Accept(string text, BlobSha? sha)
    {
        _base = text;
        _baseSha = sha;
    }

    private void SetText(string text, int caret)
    {
        if (_editor is null)
        {
            _pendingText = text;
        }
        else
        {
            _editor.SetText(text, caret);
        }
    }

    private static BlobSha? ParseSha(string? text) => text is not null && BlobSha.Parse(text) is { IsSuccess: true } sha ? sha.Value : null;
}

/// <summary>Keeps the caret on the same content when the text is replaced (F-003.2).</summary>
public static class CaretMapper
{
    /// <summary>
    /// The caret's line is followed through a line diff: unchanged lines keep the column; a changed line maps to the
    /// start of its replacement, keeping the column when it still fits.
    /// </summary>
    public static int Map(string oldText, string newText, int caret)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var oldLines = TextLines.Split(oldText);
        var newLines = TextLines.Split(newText);
        var (line, column) = Locate(oldLines, Math.Clamp(caret, 0, oldText.Length));

        var target = line;
        var delta = 0;
        foreach (var hunk in LineDiff.Diff(oldLines, newLines))
        {
            if (line < hunk.OldStart)
            {
                break;
            }

            if (line < hunk.OldEnd)
            {
                target = hunk.NewStart;
                delta = 0;
                break;
            }

            delta += hunk.NewCount - hunk.OldCount;
            target = line + delta;
        }

        if (target >= newLines.Count)
        {
            return newText.Length;
        }

        var start = newLines.Take(target).Sum(l => l.Length);
        return start + Math.Min(column, TextLines.WithoutTerminator(newLines[target]).Length);
    }

    private static (int Line, int Column) Locate(IReadOnlyList<string> lines, int caret)
    {
        var offset = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (caret < offset + lines[i].Length || i == lines.Count - 1)
            {
                return (i, caret - offset);
            }

            offset += lines[i].Length;
        }

        return (0, 0);
    }
}
