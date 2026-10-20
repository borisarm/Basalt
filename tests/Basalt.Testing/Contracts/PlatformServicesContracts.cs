using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Contracts;

/// <summary><see cref="IClock"/>: UTC time and a local zone.</summary>
public abstract class ClockContract : ContractTest<IClock>
{
    [Fact]
    public void UtcNow_IsUtc_AndTheZoneIsKnown()
    {
        Sut.UtcNow.Offset.Should().Be(TimeSpan.Zero);
        Sut.LocalZone.Should().NotBeNull();
    }
}

/// <summary><see cref="IIdGenerator"/>: distinct non-empty ids.</summary>
public abstract class IdGeneratorContract : ContractTest<IIdGenerator>
{
    [Fact]
    public void Ids_AreUniqueAndNotEmpty()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => Sut.NewId()).ToList();

        ids.Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
    }
}

/// <summary><see cref="IRandom"/>: values within their ranges.</summary>
public abstract class RandomContract : ContractTest<IRandom>
{
    [Fact]
    public void Values_StayInRange()
    {
        Enumerable.Range(0, 200).Select(_ => Sut.Next(7)).Should().OnlyContain(v => v >= 0 && v < 7);
        Enumerable.Range(0, 200).Select(_ => Sut.NextDouble()).Should().OnlyContain(v => v >= 0 && v < 1);
    }
}

/// <summary><see cref="ICultureProvider"/>: always a culture.</summary>
public abstract class CultureProviderContract : ContractTest<ICultureProvider>
{
    [Fact]
    public void Current_IsACulture() => Sut.Current.Should().NotBeNull();
}

/// <summary><see cref="IPlatformPaths"/>: rooted, distinct folders.</summary>
public abstract class PlatformPathsContract : ContractTest<IPlatformPaths>
{
    [Fact]
    public void Folders_AreRootedAndDistinct()
    {
        string[] folders = [Sut.AppDataDirectory, Sut.DefaultVaultsDirectory, Sut.LogsDirectory];

        folders.Should().OnlyContain(f => Path.IsPathRooted(f)).And.OnlyHaveUniqueItems();
    }
}

/// <summary><see cref="IDeviceInfo"/>: a stable identity and a name to show in sync history.</summary>
public abstract class DeviceInfoContract : ContractTest<IDeviceInfo>
{
    /// <summary>Another instance over the same storage (the app started again).</summary>
    protected abstract Task<IDeviceInfo> ReopenAsync();

    [Fact]
    public async Task Identity_IsStable_AndNamed()
    {
        var again = await ReopenAsync();

        Sut.DeviceId.Should().NotBe(Guid.Empty);
        again.DeviceId.Should().Be(Sut.DeviceId);
        Sut.DisplayName.Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary><see cref="IVaultLocalDataStore"/>: one folder per vault, deletions that never fail when nothing is there.</summary>
public abstract class VaultLocalDataStoreContract : ContractTest<IVaultLocalDataStore>
{
    private static readonly VaultId A = new(new Guid("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultId B = new(new Guid("bbbbbbbb-0000-4000-8000-000000000002"));

    [Fact]
    public void Folders_ArePerVault() => Sut.DirectoryOf(A).Should().NotBe(Sut.DirectoryOf(B));

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        (await Sut.DeleteAsync(A, Ct)).IsSuccess.Should().BeTrue();
        (await Sut.DeleteAsync(A, Ct)).IsSuccess.Should().BeTrue();
    }
}
