using Basalt.Domain.Notes;

namespace Basalt.Domain.Tests.Notes;

public sealed class FileClassifierTests
{
    [Theory]
    [InlineData("a.md", FileKind.Note)]
    [InlineData("Folder/B.MD", FileKind.Note)]
    [InlineData("img.png", FileKind.Image)]
    [InlineData("img.JPG", FileKind.Image)]
    [InlineData("img.jpeg", FileKind.Image)]
    [InlineData("img.gif", FileKind.Image)]
    [InlineData("img.webp", FileKind.Image)]
    [InlineData("img.svg", FileKind.Image)]
    [InlineData("img.bmp", FileKind.Image)]
    [InlineData("doc.pdf", FileKind.Pdf)]
    [InlineData("song.mp3", FileKind.Audio)]
    [InlineData("song.wav", FileKind.Audio)]
    [InlineData("song.m4a", FileKind.Audio)]
    [InlineData("song.ogg", FileKind.Audio)]
    [InlineData("song.flac", FileKind.Audio)]
    [InlineData("song.3gp", FileKind.Audio)]
    [InlineData("clip.mp4", FileKind.Video)]
    [InlineData("clip.webm", FileKind.Video)]
    [InlineData("clip.mov", FileKind.Video)]
    [InlineData("clip.mkv", FileKind.Video)]
    [InlineData("clip.ogv", FileKind.Video)]
    [InlineData("data.csv", FileKind.Other)]
    [InlineData("noext", FileKind.Other)]
    [InlineData("canvas.canvas", FileKind.Other)]
    [InlineData(".basalt/vault.json", FileKind.Internal)]
    [InlineData(".basalt/templates/a.md", FileKind.Internal)]
    [InlineData(".trash/a.md", FileKind.Trash)]
    [InlineData(".trash/img.png", FileKind.Trash)]
    [InlineData("sub/.trash/a.md", FileKind.Note)]
    [Trait("Spec", "DOM-3.2")]
    public void Classify_ByLocationAndExtension(string path, FileKind expected)
    {
        FileClassifier.Classify(VaultPath.Create(path).Value).Should().Be(expected);
    }

    [Fact]
    public void Classify_RootFolders_AreInternalAndTrash()
    {
        FileClassifier.Classify(VaultPath.Create(".basalt").Value).Should().Be(FileKind.Internal);
        FileClassifier.Classify(VaultPath.Create(".trash").Value).Should().Be(FileKind.Trash);
        FileClassifier.Classify(VaultPath.Root).Should().Be(FileKind.Other);
    }
}
