// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Enough of block-style YAML to read a GitHub Actions workflow as structure rather than as text.
///
/// The workflow's security properties are decisions about jobs, permissions, step order, and the commands
/// a step actually runs. Asserting them against raw substrings reads prose as if it were code: a sentence
/// in a comment satisfies a check that the commands never satisfied, and the test keeps passing after the
/// step it was written to protect has gone. Everything here exists so those assertions can name a job, a
/// step, and a command line instead.
/// </summary>
internal sealed class YamlNode
{
    private static readonly IReadOnlyDictionary<string, YamlNode> s_noFields =
        new Dictionary<string, YamlNode>(StringComparer.Ordinal);

    private YamlNode(string? scalar, IReadOnlyList<YamlNode>? items, IReadOnlyDictionary<string, YamlNode>? fields)
    {
        Scalar = scalar;
        Items = items;
        Fields = fields;
    }

    public string? Scalar { get; }

    public IReadOnlyList<YamlNode>? Items { get; }

    public IReadOnlyDictionary<string, YamlNode>? Fields { get; }

    /// <summary>The scalar text, or the empty string for a node that is not a scalar.</summary>
    public string Text => Scalar ?? string.Empty;

    public IReadOnlyList<YamlNode> Sequence => Items ?? [];

    public IReadOnlyList<string> Keys => Fields is null ? [] : [.. Fields.Keys];

    /// <summary>The named child, or null. Reading a missing key is how absence is asserted.</summary>
    public YamlNode? this[string key] =>
        Fields is not null && Fields.TryGetValue(key, out YamlNode? child) ? child : null;

    /// <summary>A mapping of scalars flattened for comparison, for permissions blocks and the like.</summary>
    public IReadOnlyDictionary<string, string> ScalarFields =>
        (Fields ?? s_noFields).ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.Ordinal);

    public static YamlNode Parse(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int index = 0;
        return ParseBlock(lines, ref index, 0);
    }

    private static YamlNode Scalars(string value) => new(value, null, null);

    private static YamlNode Mapping(IReadOnlyDictionary<string, YamlNode> fields) => new(null, null, fields);

    private static YamlNode SequenceOf(IReadOnlyList<YamlNode> items) => new(null, items, null);

    private static bool Skippable(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>A colon that ends a key: one followed by a space or by the end of the line.</summary>
    private static int KeyColon(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ':' && (i + 1 == line.Length || line[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'))
            ? value[1..^1]
            : value;

    private static YamlNode ParseBlock(string[] lines, ref int index, int indent)
    {
        while (index < lines.Length && Skippable(lines[index]))
        {
            index++;
        }

        if (index >= lines.Length || Indent(lines[index]) < indent)
        {
            return Scalars(string.Empty);
        }

        int actual = Indent(lines[index]);
        string trimmed = lines[index].TrimStart();

        return trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-"
            ? ParseSequence(lines, ref index, actual)
            : ParseMapping(lines, ref index, actual);
    }

    private static YamlNode ParseMapping(string[] lines, ref int index, int indent)
    {
        Dictionary<string, YamlNode> fields = new(StringComparer.Ordinal);

        while (true)
        {
            while (index < lines.Length && Skippable(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length || Indent(lines[index]) != indent)
            {
                break;
            }

            string trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            int colon = KeyColon(trimmed);
            if (colon < 0)
            {
                break;
            }

            string key = Unquote(trimmed[..colon].Trim());
            string rest = trimmed[(colon + 1)..].Trim();
            index++;

            fields[key] = rest switch
            {
                "" => ParseBlock(lines, ref index, indent + 1),
                "|" or "|-" or "|+" or ">" or ">-" or ">+" => Scalars(BlockScalar(lines, ref index, indent + 1)),
                "{}" => Mapping(new Dictionary<string, YamlNode>(StringComparer.Ordinal)),
                "[]" => SequenceOf([]),
                _ => Scalars(Unquote(rest)),
            };
        }

        return Mapping(fields);
    }

    private static YamlNode ParseSequence(string[] lines, ref int index, int indent)
    {
        List<YamlNode> items = [];

        while (true)
        {
            while (index < lines.Length && Skippable(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length || Indent(lines[index]) != indent)
            {
                break;
            }

            string trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal) && trimmed != "-")
            {
                break;
            }

            string rest = trimmed == "-" ? string.Empty : trimmed[2..].Trim();
            int itemIndent = indent + 2;

            if (rest.Length == 0)
            {
                index++;
                items.Add(ParseBlock(lines, ref index, itemIndent));
                continue;
            }

            if (KeyColon(rest) < 0)
            {
                index++;
                items.Add(Scalars(Unquote(rest)));
                continue;
            }

            // `- uses: x` is the item's first key. Re-indent it so the rest of the item's keys, which sit
            // two columns in, parse as one mapping with it.
            lines[index] = new string(' ', itemIndent) + rest;
            items.Add(ParseMapping(lines, ref index, itemIndent));
        }

        return SequenceOf(items);
    }

    private static string BlockScalar(string[] lines, ref int index, int minimumIndent)
    {
        List<string> body = [];
        int blockIndent = -1;

        while (index < lines.Length)
        {
            string line = lines[index];

            if (line.Trim().Length == 0)
            {
                body.Add(string.Empty);
                index++;
                continue;
            }

            int lineIndent = Indent(line);
            if (lineIndent < minimumIndent)
            {
                break;
            }

            blockIndent = blockIndent < 0 ? lineIndent : blockIndent;
            body.Add(line.Length > blockIndent ? line[blockIndent..] : line.TrimStart());
            index++;
        }

        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return string.Join("\n", body);
    }
}
