using OpenTail.Connectivity.RichText;

namespace OpenTail.Connectivity.Tests.RichText;

public sealed class AdfTextConverterTests
{
    [Fact]
    public void Converts_a_jira_description()
    {
        const string adf = """
        {"type":"doc","version":1,"content":[
          {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Steps"}]},
          {"type":"paragraph","content":[
            {"type":"text","text":"Reported by "},
            {"type":"mention","attrs":{"id":"5b10a2844c20165700ede21g","text":"@Ada Lovelace"}},
            {"type":"text","text":" on "},
            {"type":"date","attrs":{"timestamp":"1759312800000"}},
            {"type":"text","text":", see "},
            {"type":"text","text":"the runbook","marks":[{"type":"link","attrs":{"href":"https://wiki.example.com/runbook"}}]},
            {"type":"text","text":". "},
            {"type":"status","attrs":{"text":"IN PROGRESS","color":"blue"}}
          ]},
          {"type":"orderedList","attrs":{"order":1},"content":[
            {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Open the app"}]}]},
            {"type":"listItem","content":[
              {"type":"paragraph","content":[{"type":"text","text":"Click "},{"type":"text","text":"Save","marks":[{"type":"code"}]}]},
              {"type":"bulletList","content":[
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"twice"}]}]}
              ]}
            ]}
          ]},
          {"type":"codeBlock","attrs":{"language":"bash"},"content":[{"type":"text","text":"curl -s x\necho done"}]},
          {"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"quoted"},{"type":"hardBreak"},{"type":"text","text":"second line"}]}]},
          {"type":"table","content":[
            {"type":"tableRow","content":[
              {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Env"}]}]},
              {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Result"}]}]}]},
            {"type":"tableRow","content":[
              {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"prod"}]}]},
              {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"a|b"}]}]}]}
          ]},
          {"type":"rule"},
          {"type":"taskList","attrs":{"localId":"t"},"content":[
            {"type":"taskItem","attrs":{"localId":"1","state":"DONE"},"content":[{"type":"text","text":"triage"}]},
            {"type":"taskItem","attrs":{"localId":"2","state":"TODO"},"content":[{"type":"text","text":"fix"}]}
          ]},
          {"type":"mediaSingle","content":[{"type":"media","attrs":{"id":"m","type":"file","collection":"c","alt":"screenshot.png"}}]},
          {"type":"panel","attrs":{"panelType":"warning"},"content":[{"type":"paragraph","content":[{"type":"emoji","attrs":{"shortName":":warning:","text":"⚠️"}},{"type":"text","text":" careful"}]}]}
        ]}
        """;

        Assert.Equal("""
            ## Steps

            Reported by @Ada Lovelace on 2025-10-01, see the runbook (https://wiki.example.com/runbook). [IN PROGRESS]

            1. Open the app
            2. Click `Save`
               - twice

            ```bash
            curl -s x
            echo done
            ```

            > quoted
            > second line

            | Env | Result |
            | prod | a\|b |

            ---

            [x] triage
            [ ] fix

            [attachment: screenshot.png]

            ⚠️ careful
            """.ReplaceLineEndings("\n"), AdfTextConverter.ToText(adf));
    }

    [Fact]
    public void Unknown_nodes_keep_their_text()
    {
        const string adf = """{"type":"doc","content":[{"type":"someFutureBlock","content":[{"type":"paragraph","content":[{"type":"text","text":"still here"}]}]}]}""";
        Assert.Equal("still here", AdfTextConverter.ToText(adf));
    }

    [Fact]
    public void A_plain_string_body_is_returned_as_is()
    {
        using var doc = JsonDocument.Parse("\"plain old description\"");
        Assert.Equal("plain old description", AdfTextConverter.ToText(doc.RootElement));
    }

    [Fact]
    public void Invalid_json_fails_as_an_extractor_result()
    {
        var result = new AdfTextConverter().Extract("x.json", Encoding.UTF8.GetBytes("{not json"));
        Assert.False(result.Success);
    }
}

public sealed class ConfluenceStorageConverterTests
{
    [Fact]
    public void Converts_a_page()
    {
        const string storage = """
            <h1>Runbook</h1>
            <p>Owner: <ac:link><ri:user ri:account-id="557058:abc" /></ac:link>&nbsp;&mdash; see <ac:link><ri:page ri:content-title="Escalation" /></ac:link>
            and <a href="https://status.example.com">status</a>.</p>
            <ac:structured-macro ac:name="toc" />
            <ac:structured-macro ac:name="info"><ac:parameter ac:name="title">Before you start</ac:parameter>
              <ac:rich-text-body><p>Check the &lt;dashboard&gt; first.</p></ac:rich-text-body></ac:structured-macro>
            <ul><li>One<ul><li>One point one</li></ul></li><li>Two <ac:emoticon ac:name="smile" ac:emoji-fallback="🙂" /></li></ul>
            <ac:structured-macro ac:name="code"><ac:parameter ac:name="language">sql</ac:parameter>
              <ac:plain-text-body><![CDATA[SELECT *
            FROM incidents;]]></ac:plain-text-body></ac:structured-macro>
            <table><tbody><tr><th>Sev</th><th>Response</th></tr><tr><td><p>1</p></td><td>15 min</td></tr></tbody></table>
            <ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>page on-call</ac:task-body></ac:task></ac:task-list>
            <p><ac:image><ri:attachment ri:filename="diagram.png" /></ac:image></p>
            """;

        Assert.Equal("""
            # Runbook

            Owner: @557058:abc — see Escalation and status (https://status.example.com).

            Before you start
            Check the <dashboard> first.

            - One
              - One point one
            - Two 🙂

            ```sql
            SELECT *
            FROM incidents;
            ```

            | Sev | Response |
            | 1 | 15 min |

            [x] page on-call

            [image: diagram.png]
            """.ReplaceLineEndings("\n"), ConfluenceStorageConverter.ToText(storage));
    }

    [Fact]
    public void Markup_that_does_not_parse_is_stripped_instead()
    {
        Assert.Equal("broken\nstill text", ConfluenceStorageConverter.ToText("<p>broken</p><p>still <b>text</p>"));
    }
}

public sealed class NotionBlocksConverterTests
{
    [Fact]
    public void Converts_blocks_with_children_added_by_the_walker()
    {
        const string blocks = """
        {"object":"list","results":[
          {"type":"heading_1","heading_1":{"rich_text":[{"type":"text","plain_text":"Onboarding"}]}},
          {"type":"paragraph","paragraph":{"rich_text":[
            {"type":"text","plain_text":"Read the "},
            {"type":"text","plain_text":"handbook","href":"https://example.com/handbook"},
            {"type":"text","plain_text":" first."}]}},
          {"type":"numbered_list_item","numbered_list_item":{"rich_text":[{"plain_text":"Get a laptop"}]}},
          {"type":"numbered_list_item","numbered_list_item":{"rich_text":[{"plain_text":"Set up mail"}]},
           "children":[{"type":"bulleted_list_item","bulleted_list_item":{"rich_text":[{"plain_text":"Outlook"}]}}]},
          {"type":"to_do","to_do":{"rich_text":[{"plain_text":"Sign contract"}],"checked":true}},
          {"type":"callout","callout":{"rich_text":[{"plain_text":"Ask IT"}],"icon":{"type":"emoji","emoji":"💡"}}},
          {"type":"code","code":{"rich_text":[{"plain_text":"npm install"}],"language":"shell"}},
          {"type":"divider","divider":{}},
          {"type":"table","table":{"table_width":2},"children":[
            {"type":"table_row","table_row":{"cells":[[{"plain_text":"Day"}],[{"plain_text":"Task"}]]}},
            {"type":"table_row","table_row":{"cells":[[{"plain_text":"1"}],[{"plain_text":"Intro"}]]}}]},
          {"type":"child_page","child_page":{"title":"IT setup"}},
          {"type":"image","image":{"caption":[{"plain_text":"Office map"}],"type":"file","file":{"url":"https://s3.example/x"}}},
          {"type":"column_list","column_list":{},"children":[
            {"type":"column","column":{},"children":[{"type":"paragraph","paragraph":{"rich_text":[{"plain_text":"Left"}]}}]}]},
          {"type":"table_of_contents","table_of_contents":{}}
        ]}
        """;

        Assert.Equal("""
            # Onboarding

            Read the handbook (https://example.com/handbook) first.

            1. Get a laptop
            2. Set up mail
               - Outlook

            [x] Sign contract

            💡 Ask IT

            ```shell
            npm install
            ```

            ---

            | Day | Task |
            | 1 | Intro |

            [page: IT setup]

            [image: Office map]

            Left
            """.ReplaceLineEndings("\n"), NotionBlocksConverter.ToText(blocks));
    }

    [Fact]
    public void Numbering_restarts_after_another_block()
    {
        const string blocks = """
        [{"type":"numbered_list_item","numbered_list_item":{"rich_text":[{"plain_text":"a"}]}},
         {"type":"paragraph","paragraph":{"rich_text":[{"plain_text":"break"}]}},
         {"type":"numbered_list_item","numbered_list_item":{"rich_text":[{"plain_text":"b"}]}}]
        """;
        Assert.Equal("1. a\n\nbreak\n\n1. b", NotionBlocksConverter.ToText(blocks));
    }
}

public sealed class SlackMarkupConverterTests
{
    [Fact]
    public void Resolves_mentions_channels_links_and_entities()
    {
        var names = new Dictionary<string, string> { ["U012AB3CD"] = "ada", ["C024BE91L"] = "ops" };
        const string text = "<@U012AB3CD> and <@U999> in <#C024BE91L> / <#C111|general>: <!here> <!subteam^SAZ94GDB8|@oncall> " +
                            "see <https://example.com/a?b=1&amp;c=2|the doc> or <https://example.com> mail <mailto:x@example.com|x@example.com> " +
                            "at <!date^1392734382^{date_short}|Feb 18, 2014> &lt;not a tag&gt; &amp;lt;";
        Assert.Equal(
            "@ada and @U999 in #ops / #general: @here @oncall see the doc (https://example.com/a?b=1&c=2) or https://example.com " +
            "mail x@example.com at Feb 18, 2014 <not a tag> &lt;",
            SlackMarkupConverter.ToText(text, names));
    }

    [Fact]
    public void A_date_without_fallback_is_written_in_utc()
    {
        Assert.Equal("2014-02-18 14:39 UTC", SlackMarkupConverter.ToText("<!date^1392734382^{date}>"));
    }
}
