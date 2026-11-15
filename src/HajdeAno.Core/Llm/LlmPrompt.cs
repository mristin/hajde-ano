using System.Text;

namespace HajdeAno.Core.Llm;

/// <summary>Prompt and output grammar for entity extraction.</summary>
public static class LlmPrompt
{
    /// <summary>Types the model may output (GBNF grammar and prompt agree on this list).</summary>
    public static IReadOnlyList<string> Labels { get; } =
        Categories.All.Select(c => c.LlmLabel()).ToArray();

    public static string SystemPrompt { get; } = BuildSystemPrompt();

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "You are a privacy filter. Find every piece of private or identifying information in the user's text."
        );
        sb.AppendLine(
            "Return ONLY a JSON array. Each element is {\"text\": \"<exact substring of the text>\", \"type\": \"<TYPE>\"}."
        );
        sb.AppendLine();
        sb.AppendLine("Types:");
        sb.AppendLine("- PERSON: full names, first names, surnames, nicknames of people");
        sb.AppendLine(
            "- FAMILY: a family referred to by surname, e.g. \"Familie Meier\" -> \"Meier\""
        );
        sb.AppendLine(
            "- ORGANIZATION: companies, foundations, housing cooperatives, associations, landlords, property managers, banks, insurers, employers, schools"
        );
        sb.AppendLine("- ORG_ABBREVIATION: abbreviations of organizations, e.g. \"SVA\", \"GBL\"");
        sb.AppendLine(
            "- GOVERNMENT_AGENCY: authorities, offices, social services, migration office"
        );
        sb.AppendLine("- COURT: courts and conciliation authorities");
        sb.AppendLine("- ADDRESS: street and house number, P.O. box");
        sb.AppendLine("- CITY, POSTAL_CODE, COUNTRY, NATIONALITY");
        sb.AppendLine("- PHONE, EMAIL, URL");
        sb.AppendLine("- DATE, DATE_OF_BIRTH, AMOUNT (money)");
        sb.AppendLine(
            "- IBAN, CASE_NUMBER, DEBT_COLLECTION_REF, CUSTOMER_NUMBER, SOCIAL_SECURITY_NUMBER, PERMIT_NUMBER"
        );
        sb.AppendLine("- OTHER: any other detail that could identify a person");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine(
            "- Copy each span character by character exactly as it appears in the text, including spelling mistakes. Never correct, translate or normalize."
        );
        sb.AppendLine("- Do not include titles such as Herr, Frau, Dr. in PERSON spans.");
        sb.AppendLine("- List each distinct span only once.");
        sb.AppendLine("- Names can come from any language and culture.");
        sb.AppendLine("- When unsure whether something is private, include it.");
        sb.AppendLine("- If there is nothing private, return [].");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Synthetic one-shot example (user text, expected answer).</summary>
    public static (string User, string Assistant) Example { get; } =
        (
            "Anna Müller wohnt in Zürich. Die Stiftung Beispiel hat ihr eine Wohnung vermittelt. Die Verwaltung verlangt CHF 4500 für die Räumung.",
            "[{\"text\": \"Anna Müller\", \"type\": \"PERSON\"}, {\"text\": \"Zürich\", \"type\": \"CITY\"}, {\"text\": \"Stiftung Beispiel\", \"type\": \"ORGANIZATION\"}, {\"text\": \"CHF 4500\", \"type\": \"AMOUNT\"}]"
        );

    /// <summary>
    /// Build a Qwen3 (ChatML) prompt in non-thinking mode. The empty think block is what
    /// the official chat template emits for <c>enable_thinking=False</c>.
    /// </summary>
    public static string BuildChatMlPrompt(string text)
    {
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n").Append(SystemPrompt).Append("<|im_end|>\n");
        sb.Append("<|im_start|>user\n").Append(Example.User).Append("<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n<think>\n\n</think>\n\n")
            .Append(Example.Assistant)
            .Append("<|im_end|>\n");
        sb.Append("<|im_start|>user\n").Append(text).Append("<|im_end|>\n");
        sb.Append("<|im_start|>assistant\n<think>\n\n</think>\n\n");
        return sb.ToString();
    }

    /// <summary>GBNF grammar constraining the output to a JSON array of entities.</summary>
    public static string Grammar { get; } = BuildGrammar();

    private static string BuildGrammar()
    {
        var types = string.Join(" | ", Labels.Select(l => $"\"\\\"{l}\\\"\""));
        return $$"""
            root   ::= "[" ws ( item ( "," ws item )* )? ws "]"
            item   ::= "{" ws "\"text\"" ws ":" ws string ws "," ws "\"type\"" ws ":" ws type ws "}"
            type   ::= {{types}}
            string ::= "\"" char{1,200} "\""
            char   ::= [^"\\\x00-\x1f] | "\\" ["\\/nt]
            ws     ::= [ \n]{0,2}
            """;
    }
}
