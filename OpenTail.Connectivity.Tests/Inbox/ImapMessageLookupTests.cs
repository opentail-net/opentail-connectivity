using MailKit;

namespace OpenTail.Connectivity.Tests.Inbox;

/// <summary>IMAP's HEADER search is a substring match; the lookup keeps only exact Message-IDs and refuses ambiguity.</summary>
public sealed class ImapMessageLookupTests
{
    [Fact]
    public void A_longer_id_that_contains_the_wanted_one_is_not_a_match()
    {
        var hit = ImapMessageLookup.Pick(
            [(new UniqueId(1), "abc@example.com.evil"), (new UniqueId(2), "abc@example.com"), (new UniqueId(3), "xabc@example.com")],
            "abc@example.com");

        Assert.Equal(new UniqueId(2), hit);
    }

    [Fact]
    public void Angle_brackets_and_spaces_do_not_matter_on_either_side()
        => Assert.Equal(new UniqueId(7), ImapMessageLookup.Pick([(new UniqueId(7), " <abc@example.com>")], "<abc@example.com>"));

    [Fact]
    public void Only_substring_matches_means_no_message()
        => Assert.Null(ImapMessageLookup.Pick([(new UniqueId(1), "abc@example.com.evil"), (new UniqueId(2), null)], "abc@example.com"));

    [Fact]
    public void Two_messages_with_the_same_id_are_refused_rather_than_both_changed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ImapMessageLookup.Pick([(new UniqueId(1), "abc@example.com"), (new UniqueId(2), "abc@example.com")], "abc@example.com"));
        Assert.Contains("2 messages", ex.Message);
    }

    [Fact]
    public void Case_is_kept_since_the_left_part_of_a_message_id_is_case_sensitive()
        => Assert.Null(ImapMessageLookup.Pick([(new UniqueId(1), "ABC@example.com")], "abc@example.com"));
}
