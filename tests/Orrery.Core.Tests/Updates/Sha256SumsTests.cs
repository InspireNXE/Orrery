using Orrery.Core.Settings;
using Orrery.Core.Updates;
using Xunit;

namespace Orrery.Core.Tests.Updates;

public class Sha256SumsTests
{
    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string B = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void Reads_sha256sum_lines()
    {
        var sums = Sha256Sums.Parse($"# made by CI\r\n{A}  Orrery-Setup-0.2.0.exe\r\n\r\n{B} *Orrery-0.2.0-win-x64-portable.zip\nnot a line\n{A}  \n");

        Assert.Equal(2, sums.Count);
        Assert.Equal(A, sums["Orrery-Setup-0.2.0.exe"]);
        Assert.Equal(B.ToLowerInvariant(), sums["Orrery-0.2.0-win-x64-portable.zip"]);
    }

    [Fact]
    public void A_file_listed_twice_with_different_sums_fails()
    {
        Assert.Equal(A, Sha256Sums.Parse($"{A}  x.zip\n{A}  x.zip\n")["x.zip"]);
        Assert.Throws<UpdateException>(() => Sha256Sums.Parse($"{A}  x.zip\n{B}  x.zip\n"));
    }
}

public class UpdateScheduleTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Due_at_first_and_then_once_a_day()
    {
        Assert.True(UpdateSchedule.IsCheckDue(new AppSettings(), Now));
        Assert.False(UpdateSchedule.IsCheckDue(new AppSettings { LastUpdateCheck = Now.AddHours(-23) }, Now));
        Assert.True(UpdateSchedule.IsCheckDue(new AppSettings { LastUpdateCheck = Now.AddHours(-24) }, Now));
        Assert.Equal(TimeSpan.FromHours(1), UpdateSchedule.TimeUntilDue(new AppSettings { LastUpdateCheck = Now.AddHours(-23) }, Now));
        Assert.Equal(TimeSpan.Zero, UpdateSchedule.TimeUntilDue(new AppSettings(), Now));
    }

    [Fact]
    public void Never_when_checks_are_off()
    {
        var off = new AppSettings { UpdateChecks = false };
        Assert.False(UpdateSchedule.IsCheckDue(off, Now));
        Assert.Null(UpdateSchedule.TimeUntilDue(off, Now));
    }

    [Fact]
    public void A_last_check_in_the_future_doesnt_stop_checks()
    {
        Assert.True(UpdateSchedule.IsCheckDue(new AppSettings { LastUpdateCheck = Now.AddDays(3) }, Now));
    }
}
