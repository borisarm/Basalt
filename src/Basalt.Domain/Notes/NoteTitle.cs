namespace Basalt.Domain.Notes;

/// <summary>Visible title of a note: the file name without extension (spec 02 §4).</summary>
public readonly record struct NoteTitle(string Value)
{
    public static NoteTitle FromPath(VaultPath path) => new(path.NameWithoutExtension);

    public override string ToString() => Value;
}
