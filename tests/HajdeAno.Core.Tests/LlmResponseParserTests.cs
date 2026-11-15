using HajdeAno.Core.Llm;

namespace HajdeAno.Core.Tests;

public class LlmResponseParserTests
{
    [Fact]
    public void Parses_valid_json()
    {
        var r = LlmResponseParser.Parse(
            "[{\"text\": \"Anna Müller\", \"type\": \"PERSON\"}, {\"text\": \"Zürich\", \"type\": \"CITY\"}]"
        );
        Assert.True(r.WasValidJson);
        Assert.Equal(
            [new RawEntity("Anna Müller", Category.Person), new RawEntity("Zürich", Category.City)],
            r.Entities
        );
    }

    [Fact]
    public void Strips_code_fences_and_think_blocks()
    {
        var r = LlmResponseParser.Parse(
            "<think>\nhmm\n</think>\n```json\n[{\"text\": \"Anna\", \"type\": \"PERSON\"}]\n```"
        );
        Assert.True(r.WasValidJson);
        Assert.Single(r.Entities);
    }

    [Fact]
    public void Salvages_truncated_output()
    {
        var r = LlmResponseParser.Parse(
            "[{\"text\": \"Anna\", \"type\": \"PERSON\"}, {\"type\": \"CITY\", \"text\": \"Bern\"}, {\"text\": \"Zür"
        );
        Assert.False(r.WasValidJson);
        Assert.Equal(2, r.Entities.Count);
    }

    [Fact]
    public void Handles_escapes()
    {
        var r = LlmResponseParser.Parse(
            "[{\"text\": \"\\\"Zur Linde\\\" GmbH\", \"type\": \"ORGANIZATION\"}]"
        );
        Assert.Equal("\"Zur Linde\" GmbH", r.Entities[0].Text);
    }

    [Fact]
    public void Unknown_types_map_to_other_and_synonyms_are_understood()
    {
        var r = LlmResponseParser.Parse(
            "[{\"text\": \"a\", \"type\": \"SPACESHIP\"}, {\"text\": \"b\", \"type\": \"company\"}, {\"text\": \"c\"}]"
        );
        Assert.Equal(
            [Category.Other, Category.Organization, Category.Other],
            r.Entities.Select(e => e.Category)
        );
    }

    [Fact]
    public void Garbage_yields_nothing()
    {
        var r = LlmResponseParser.Parse("I cannot help with that.");
        Assert.False(r.WasValidJson);
        Assert.Empty(r.Entities);
    }

    [Fact]
    public void Deduplicates()
    {
        var r = LlmResponseParser.Parse(
            "[{\"text\": \"Anna\", \"type\": \"PERSON\"}, {\"text\": \"Anna\", \"type\": \"PERSON\"}]"
        );
        Assert.Single(r.Entities);
    }

    [Fact]
    public void Grammar_lists_all_labels()
    {
        foreach (var label in LlmPrompt.Labels)
        {
            Assert.Contains($"\"\\\"{label}\\\"\"", LlmPrompt.Grammar, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Prompt_uses_non_thinking_chat_template()
    {
        var p = LlmPrompt.BuildChatMlPrompt("Text");
        Assert.EndsWith(
            "<|im_start|>assistant\n<think>\n\n</think>\n\n",
            p,
            StringComparison.Ordinal
        );
    }
}
