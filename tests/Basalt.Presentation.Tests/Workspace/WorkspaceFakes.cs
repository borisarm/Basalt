using System.Text;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Domain.Common;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Presentation.Workspace;

namespace Basalt.Presentation.Tests.Workspace;

/// <summary>Notes on "disk" behind the ReadNote/SaveNote use cases, with SHA-based optimistic concurrency.</summary>
internal sealed class FakeNotes : IQueryHandler<ReadNote, NoteDocument>, ICommandHandler<SaveNote, BlobSha>
{
    private readonly Dictionary<VaultPath, string> _files = [];

    public int Saves { get; private set; }

    public Error? NextSaveError { get; set; }

    public static BlobSha Sha(string text) => GitBlobHasher.Compute(Encoding.UTF8.GetBytes(text));

    public void Write(VaultPath path, string text) => _files[path] = text;

    public string Read(VaultPath path) => _files[path];

    public Task<Result<NoteDocument>> HandleAsync(ReadNote query, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(query.Path, out var text)
            ? Result.Success(new NoteDocument(query.Path, NoteContent.FromText(text), NoteMetadata.Empty, Sha(text)))
            : Result.Failure<NoteDocument>(new Error(ErrorCodes.EntryNotFound, "missing").With("path", query.Path.ToString())));

    public Task<Result<BlobSha>> HandleAsync(SaveNote command, CancellationToken ct)
    {
        if (NextSaveError is { } error)
        {
            NextSaveError = null;
            return Task.FromResult(Result.Failure<BlobSha>(error));
        }

        if (command.ExpectedSha is { } expected && (!_files.TryGetValue(command.Path, out var current) || Sha(current) != expected))
        {
            var disk = _files.GetValueOrDefault(command.Path);
            return Task.FromResult(Result.Failure<BlobSha>(new Error(ErrorCodes.NoteModifiedExternally, "changed")
                .With("currentText", disk).With("currentSha", disk is null ? null : Sha(disk).ToString())));
        }

        _files[command.Path] = command.Text;
        Saves++;
        return Task.FromResult(Result.Success(Sha(command.Text)));
    }
}

/// <summary>The editor view seen through <see cref="IEditorBridge"/>.</summary>
internal sealed class FakeEditor : IEditorBridge
{
    public string Text { get; private set; } = string.Empty;

    public int Caret { get; set; }

    public int SetTextCalls { get; private set; }

    public string GetText() => Text;

    public void SetText(string text, int caret)
    {
        Text = text;
        Caret = caret;
        SetTextCalls++;
    }

    /// <summary>Simulates typing: changes the text and notifies the tab like the real view does.</summary>
    public void Type(NoteTabViewModel tab, string text, int? caret = null)
    {
        Text = text;
        Caret = caret ?? text.Length;
        tab.OnTextChanged();
    }
}
