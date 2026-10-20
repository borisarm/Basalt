using Basalt.Domain.Notes;

namespace Basalt.Presentation.Workspace;

/// <summary>The file shown by the active tab changed (null: no file).</summary>
public sealed record ActiveFileChanged(VaultPath? Path);

/// <summary>Runs a search in the search panel (tags, saved searches…).</summary>
public sealed record SearchRequested(string Query);

/// <summary>Expands the explorer to a file or folder and selects it.</summary>
public sealed record RevealInExplorerRequested(VaultPath Path);

/// <summary>The editor's caret moved to another line (0-based) of the active note.</summary>
public sealed record CaretLineChanged(VaultPath Path, int Line);
