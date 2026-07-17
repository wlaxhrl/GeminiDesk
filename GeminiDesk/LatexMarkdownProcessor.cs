using System.Text;
using System.Text.RegularExpressions;

namespace GeminiDesk;

internal sealed record LatexExpression(string Token, string Formula, bool IsDisplay);

internal sealed record LatexMarkdownDocument(
    string Markdown,
    IReadOnlyDictionary<string, LatexExpression> Expressions);

internal static class LatexMarkdownProcessor
{
    private static readonly Regex BracketDisplayMath = new(
        @"(?<!\\)\\{1,2}\[(?<formula>[\s\S]*?)(?<!\\)\\{1,2}\]",
        RegexOptions.Compiled);

    private static readonly Regex DollarDisplayMath = new(
        @"(?<![\\$])\$\$(?<formula>[\s\S]+?)(?<!\\)\$\$(?!\$)",
        RegexOptions.Compiled);

    private static readonly Regex ParenthesisInlineMath = new(
        @"(?<!\\)\\{1,2}\((?<formula>[^\r\n]*?)(?<!\\)\\{1,2}\)",
        RegexOptions.Compiled);

    private static readonly Regex DollarInlineMath = new(
        @"(?<![\\$])\$(?!\$|\s)(?<formula>[^\r\n$]+?)(?<![\\\s])\$(?!\$)",
        RegexOptions.Compiled);

    public static LatexMarkdownDocument Prepare(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return new LatexMarkdownDocument(
                source ?? string.Empty,
                new Dictionary<string, LatexExpression>());
        }

        var protectedCode = new Dictionary<string, string>(StringComparer.Ordinal);
        var markdown = ProtectCode(source, protectedCode);
        var expressions = new Dictionary<string, LatexExpression>(StringComparer.Ordinal);

        markdown = ReplaceMath(markdown, BracketDisplayMath, true, expressions);
        markdown = ReplaceMath(markdown, DollarDisplayMath, true, expressions);
        markdown = ReplaceMath(markdown, ParenthesisInlineMath, false, expressions);
        markdown = ReplaceMath(markdown, DollarInlineMath, false, expressions);

        foreach (var pair in protectedCode)
        {
            markdown = markdown.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }

        return new LatexMarkdownDocument(markdown, expressions);
    }

    private static string ReplaceMath(
        string markdown,
        Regex pattern,
        bool isDisplay,
        IDictionary<string, LatexExpression> expressions)
    {
        return pattern.Replace(markdown, match =>
        {
            var formula = match.Groups["formula"].Value.Trim();
            if (formula.Length == 0)
            {
                return match.Value;
            }

            var token = $"BUNNYDESKMATH{(isDisplay ? "BLOCK" : "INLINE")}{expressions.Count:D6}END";
            expressions[token] = new LatexExpression(token, formula, isDisplay);

            return isDisplay ? $"\n\n{token}\n\n" : token;
        });
    }

    private static string ProtectCode(string source, IDictionary<string, string> protectedCode)
    {
        var output = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            if (TryReadFencedCode(source, index, out var fenceEnd))
            {
                AppendProtectedSegment(source, index, fenceEnd, protectedCode, output);
                index = fenceEnd;
                continue;
            }

            if (source[index] == '`' && TryReadInlineCode(source, index, out var inlineEnd))
            {
                AppendProtectedSegment(source, index, inlineEnd, protectedCode, output);
                index = inlineEnd;
                continue;
            }

            output.Append(source[index]);
            index++;
        }

        return output.ToString();
    }

    private static void AppendProtectedSegment(
        string source,
        int start,
        int end,
        IDictionary<string, string> protectedCode,
        StringBuilder output)
    {
        var token = $"BUNNYDESKCODE{protectedCode.Count:D6}END";
        protectedCode[token] = source[start..end];
        output.Append(token);
    }

    private static bool TryReadFencedCode(string source, int start, out int end)
    {
        end = start;
        if (start > 0 && source[start - 1] != '\n')
        {
            return false;
        }

        var markerStart = start;
        while (markerStart < source.Length && markerStart - start < 3 && source[markerStart] == ' ')
        {
            markerStart++;
        }

        if (markerStart >= source.Length || (source[markerStart] != '`' && source[markerStart] != '~'))
        {
            return false;
        }

        var marker = source[markerStart];
        var openingRun = CountRun(source, markerStart, marker);
        if (openingRun < 3)
        {
            return false;
        }

        var openingLineEnd = FindLineEnd(source, markerStart + openingRun);
        if (openingLineEnd >= source.Length)
        {
            end = source.Length;
            return true;
        }

        var lineStart = openingLineEnd + 1;
        while (lineStart < source.Length)
        {
            var lineEnd = FindLineEnd(source, lineStart);
            var cursor = lineStart;

            while (cursor < lineEnd && cursor - lineStart < 3 && source[cursor] == ' ')
            {
                cursor++;
            }

            var closingRun = cursor < lineEnd && source[cursor] == marker
                ? CountRun(source, cursor, marker)
                : 0;

            if (closingRun >= openingRun && IsWhitespaceOnly(source, cursor + closingRun, lineEnd))
            {
                end = lineEnd < source.Length ? lineEnd + 1 : lineEnd;
                return true;
            }

            if (lineEnd >= source.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        end = source.Length;
        return true;
    }

    private static bool TryReadInlineCode(string source, int start, out int end)
    {
        end = start;
        var openingRun = CountRun(source, start, '`');
        var cursor = start + openingRun;

        while (cursor < source.Length)
        {
            if (source[cursor] != '`')
            {
                cursor++;
                continue;
            }

            var closingRun = CountRun(source, cursor, '`');
            if (closingRun == openingRun)
            {
                end = cursor + closingRun;
                return true;
            }

            cursor += closingRun;
        }

        return false;
    }

    private static int CountRun(string source, int start, char value)
    {
        var cursor = start;
        while (cursor < source.Length && source[cursor] == value)
        {
            cursor++;
        }

        return cursor - start;
    }

    private static int FindLineEnd(string source, int start)
    {
        var lineFeed = source.IndexOf('\n', start);
        return lineFeed < 0 ? source.Length : lineFeed;
    }

    private static bool IsWhitespaceOnly(string source, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(source[index]))
            {
                return false;
            }
        }

        return true;
    }
}
