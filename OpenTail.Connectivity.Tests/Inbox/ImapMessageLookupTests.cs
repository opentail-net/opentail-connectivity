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

    private static readonly ImapSettings Alice = new("imap.example.com", 993, true, "alice", "pw");
    private static readonly ImapSettings Bob = new("imap.example.com", 993, true, "bob", "pw");

    [Fact]
    public void A_message_without_a_message_id_gets_the_same_id_every_run_and_one_per_account()
    {
        Assert.Equal(ImapMessageLookup.StableId(Alice, "INBOX", 100, 7), ImapMessageLookup.StableId(Alice, "INBOX", 100, 7));
        Assert.NotEqual(ImapMessageLookup.StableId(Alice, "INBOX", 100, 7), ImapMessageLookup.StableId(Bob, "INBOX", 100, 7));
        Assert.NotEqual(ImapMessageLookup.StableId(Alice, "INBOX", 100, 7), ImapMessageLookup.StableId(Alice, "Archive", 100, 7));
    }

    [Fact]
    public void A_stable_id_resolves_only_for_its_own_account_folder_and_numbering()
    {
        var id = ImapMessageLookup.StableId(Alice, "INBOX", 100, 7);

        Assert.Equal(7u, ImapMessageLookup.StableUid(id, Alice, "INBOX", 100));
        Assert.Null(ImapMessageLookup.StableUid(id, Bob, "INBOX", 100));     // another account
        Assert.Null(ImapMessageLookup.StableUid(id, Alice, "INBOX", 101));   // renumbered: UID 7 is another message now
        Assert.Null(ImapMessageLookup.StableUid(id, Alice, "INBOX", 10));    // a prefix of the numbering is not it
        Assert.Null(ImapMessageLookup.StableUid(id + "x", Alice, "INBOX", 100));
    }
}
