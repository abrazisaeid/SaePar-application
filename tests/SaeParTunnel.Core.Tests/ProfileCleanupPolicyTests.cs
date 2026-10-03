using SaeParTunnel.Core.Models;
using SaeParTunnel.Core.Services;

namespace SaeParTunnel.Core.Tests;

public sealed class ProfileCleanupPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0);
    private static ConfigProfile Failed(int failures = 3, int ageDays = 8) => new()
    {
        Id = Guid.NewGuid().ToString(), Health = ProfileHealth.Failed,
        FailureCount = failures, FirstSeen = Now.AddDays(-ageDays), LastTested = Now
    };
    private static ConfigProfile Working(int minutesAgo = 1) => new()
    {
        Health = ProfileHealth.Working, LastTested = Now.AddMinutes(-minutesAgo)
    };

    [Fact]
    public void OldRepeatedFailureIsRemovedButTemporaryFailuresAndNewServersRemain()
    {
        var old = Failed();
        var recent = Failed(ageDays: 1);
        var temporary = Failed(failures: 1);
        var result = ProfileCleanupPolicy.FindRemovable(new[] { old, recent, temporary, Working() }, Now);
        Assert.Equal(new[] { old }, result);
    }

    [Fact]
    public void OfflineRunDoesNotDeleteServers()
    {
        Assert.Empty(ProfileCleanupPolicy.FindRemovable(new[] { Failed(), Failed() }, Now));
        Assert.Empty(ProfileCleanupPolicy.FindRemovable(new[] { Failed(), Working(16) }, Now));
    }

    [Fact]
    public void ActiveServerIsProtectedEvenIfItsCachedHealthIsFailed()
    {
        var active = Failed();
        Assert.Empty(ProfileCleanupPolicy.FindRemovable(new[] { active, Working() }, Now, active.Id));
    }

    [Fact]
    public void SuccessfulRetestAndUntestedProfilesAreKept()
    {
        var recovered = Failed(); recovered.Health = ProfileHealth.Working; recovered.FailureCount = 0;
        var untested = Failed(); untested.Health = ProfileHealth.Untested;
        Assert.Empty(ProfileCleanupPolicy.FindRemovable(new[] { recovered, untested }, Now));
    }
}
