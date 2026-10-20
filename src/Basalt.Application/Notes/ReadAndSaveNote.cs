using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Markdown;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Notes;

/// <summary>A note opened for editing: its text, metadata and the SHA used for optimistic concurrency.</summary>
public sealed record NoteDocument(VaultPath Path, NoteContent Content, NoteMetadata Metadata, BlobSha Sha);

/// <returns><c>entry.not_found</c> when the note does not exist.</returns>
public sealed record ReadNote(VaultId VaultId, VaultPath Path) : IQuery<NoteDocument>, IVaultRequest;

public sealed class ReadNoteHandler(IVaultSessions sessions, IMarkdownAnalyzer analyzer) : IQueryHandler<ReadNote, NoteDocument>
{
    public async Task<Result<NoteDocument>> HandleAsync(ReadNote query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var file = await NoteFiles.ReadAsync(session.Value.FileSystem, query.Path, ct).ConfigureAwait(false);
        return file.Map(f => new NoteDocument(f.Path, f.Content, analyzer.Analyze(f.Content.Text), f.Sha));
    }
}

/// <summary>
/// Saves edited text (F-003.1) keeping the file's line endings and BOM. With <paramref name="ExpectedSha"/> (the SHA the
/// editor loaded) the save fails with <c>note.modified_externally</c> — carrying the current text in the error
/// metadata (<c>currentText</c>, <c>currentSha</c>) — when the file changed meanwhile, so the UI can offer to merge.
/// </summary>
public sealed record SaveNote(VaultId VaultId, VaultPath Path, string Text, BlobSha? ExpectedSha) : ICommand<BlobSha>, IVaultRequest;

public sealed class SaveNoteHandler(IVaultSessions sessions, IEventBus events) : ICommandHandler<SaveNote, BlobSha>
{
    public async Task<Result<BlobSha>> HandleAsync(SaveNote command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var fs = session.Value.FileSystem;
        var current = await NoteFiles.ReadAsync(fs, command.Path, ct).ConfigureAwait(false);
        if (current.IsFailure && current.Error.Code != ErrorCodes.EntryNotFound)
        {
            return current.Error;
        }

        if (command.ExpectedSha is { } expected && (current.IsFailure || current.Value.Sha != expected))
        {
            return ModifiedExternally(current.IsSuccess ? current.Value : null);
        }

        var content = current.IsSuccess ? current.Value.Content.WithText(command.Text) : NoteContent.FromText(command.Text);
        var bytes = content.ToBytes();
        var written = await fs.WriteAllBytesAsync(command.Path, bytes, current.IsSuccess ? current.Value.Sha : null, ct).ConfigureAwait(false);
        if (written.IsFailure)
        {
            if (written.Error.Code != ErrorCodes.FsConcurrentModification)
            {
                return written.Error;
            }

            var latest = await NoteFiles.ReadAsync(fs, command.Path, ct).ConfigureAwait(false);
            return ModifiedExternally(latest.IsSuccess ? latest.Value : null);
        }

        await events.PublishAsync(new NoteSaved(command.VaultId, command.Path), ct).ConfigureAwait(false);
        return GitBlobHasher.Compute(bytes);
    }

    private static Error ModifiedExternally(NoteFile? current) =>
        new Error(ErrorCodes.NoteModifiedExternally, "The note changed on disk since it was loaded.")
            .With("currentText", current?.Content.Text)
            .With("currentSha", current?.Sha.ToString());
}
