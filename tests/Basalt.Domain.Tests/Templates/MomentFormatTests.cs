using System.Globalization;
using Basalt.Domain.Templates;

namespace Basalt.Domain.Tests.Templates;

public sealed class MomentFormatTests
{
    private static readonly DateTime Sample = new(2026, 10, 19, 15, 42, 33, DateTimeKind.Unspecified);
    private static readonly DateTime Morning = new(2026, 3, 5, 9, 7, 8, DateTimeKind.Unspecified);

    public static TheoryData<string, string, DateTime, string> Cases => new()
    {
        { "YYYY-MM-DD", "en-US", Sample, "2026-10-19" },
        { "YYYY/YYYY-MM-DD", "en-US", Sample, "2026/2026-10-19" },
        { "YY", "en-US", Sample, "26" },
        { "M/D/YYYY", "en-US", Morning, "3/5/2026" },
        { "MM/DD", "en-US", Morning, "03/05" },
        { "MMM", "en-US", Sample, "Oct" },
        { "MMMM", "en-US", Sample, "October" },
        { "ddd", "en-US", Sample, "Mon" },
        { "dddd", "en-US", Sample, "Monday" },
        { "dddd D MMMM YYYY", "es-ES", Sample, "lunes 19 octubre 2026" },
        { "dddd, D [de] MMMM [de] YYYY", "es-ES", Sample, "lunes, 19 de octubre de 2026" },
        { "MMMM", "es-ES", Morning, "marzo" },
        { "HH:mm:ss", "en-US", Sample, "15:42:33" },
        { "H:m:s", "en-US", Morning, "9:7:8" },
        { "hh:mm A", "en-US", Sample, "03:42 PM" },
        { "h a", "en-US", Morning, "9 am" },
        { "[Week of] YYYY", "en-US", Sample, "Week of 2026" },
        { "[YYYY]", "en-US", Sample, "YYYY" },
        { "YYYYMMDDHHmmss", "en-US", Sample, "20261019154233" },
        { "Q", "en-US", Sample, "4" },
        { "Do", "en-US", Morning, "5th" },
        { "Do", "en-US", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), "1st" },
        { "Do", "en-US", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified), "2nd" },
        { "Do", "en-US", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Unspecified), "3rd" },
        { "Do", "en-US", new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Unspecified), "11th" },
        { "Do", "en-US", new DateTime(2026, 1, 22, 0, 0, 0, DateTimeKind.Unspecified), "22nd" },
        { "Do", "es-ES", Morning, "5º" },
        { "x-y_z", "en-US", Sample, "x-y_z" },
        { "", "en-US", Sample, "" },
        { "[unclosed", "en-US", Sample, "unclosed" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("Spec", "DOM-9")]
    public void Format_MomentTokens(string format, string culture, DateTime value, string expected)
    {
        MomentFormat.Format(value, format, CultureInfo.GetCultureInfo(culture)).Should().Be(expected);
    }

    [Theory]
    [InlineData("YYYY-MM-DD", "yyyy'-'MM'-'dd")]
    [InlineData("dddd D MMMM YYYY", "dddd' 'd' 'MMMM' 'yyyy")]
    [InlineData("HH:mm", "HH':'mm")]
    [InlineData("[at] h A", "'at'' 'h' 'tt")]
    [InlineData("YY M D H m s", "yy' 'M' 'd' 'H' 'm' 's")]
    public void ToDotNet_TranslatesTokens(string moment, string expected)
    {
        MomentFormat.ToDotNet(moment).Should().Be(expected);
    }

    [Fact]
    public void ToDotNet_FormatsLikeOwnFormatter()
    {
        var culture = CultureInfo.GetCultureInfo("es-ES");
        var format = "dddd D MMMM YYYY HH:mm";

        Sample.ToString(MomentFormat.ToDotNet(format), culture).Should().Be(MomentFormat.Format(Sample, format, culture));
    }

    [Fact]
    public void RejectsNulls()
    {
        FluentActions.Invoking(() => MomentFormat.Format(Sample, null!, CultureInfo.InvariantCulture)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => MomentFormat.Format(Sample, "Y", null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => MomentFormat.ToDotNet(null!)).Should().Throw<ArgumentNullException>();
    }
}
