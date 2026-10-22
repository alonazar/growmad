using NomadInbox.Core.Domain;

namespace NomadInbox.Tests;

public class InboxSettingsTests
{
    [Fact]
    public void Empty_allowlist_accepts_everyone()
    {
        Assert.True(new InboxSettings().IsSenderAllowed("anyone@example.com"));
    }

    [Theory]
    [InlineData("me@gmail.com", true)]
    [InlineData("ME@Gmail.com", true)]
    [InlineData("kid@family.org", true)]
    [InlineData("other@gmail.com", false)]
    [InlineData("evil@notfamily.org.example", false)]
    public void Allowlist_matches_addresses_and_domains(string sender, bool expected)
    {
        var settings = new InboxSettings { AllowedSenders = "me@gmail.com\r\n @family.org ; " };

        Assert.Equal(expected, settings.IsSenderAllowed(sender));
    }
}
