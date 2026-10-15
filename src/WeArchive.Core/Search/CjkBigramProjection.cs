namespace WeArchive.Core.Search;

/// <summary>
/// The deterministic CJK bigram projection of <c>docs/SEARCH_SPEC.md</c> section 4.2 (ADR 0013).
/// <para>
/// unicode61 treats a maximal run of CJK ideographs as one long token, so a 2-character query can
/// never match inside a longer run. This projection rewrites every BMP CJK ideograph run of two or
/// more characters into its overlapping bigrams (order preserved, single-space separated) and
/// inserts a separating space at every CJK↔non-CJK boundary, so unicode61 token adjacency becomes
/// exact CJK substring semantics. A run of one ideograph is copied verbatim, and every other
/// character is copied verbatim.
/// </para>
/// <para>
/// The projection is applied to the searchable text at index time and to every query term at query
/// time. It never touches canonical storage: <c>semantic_text</c>, payloads and every exported
/// artifact remain exactly as <c>docs/MESSAGE_SCHEMA.md</c> defines them. Changing the algorithm
/// changes match behavior and therefore requires a specification update, a re-run of the pinned
/// benchmark tests (<c>FtsSearchBenchmarkTests</c>) and a rebuild of the derived index
/// (docs/SEARCH_SPEC.md section 4.2).
/// </para>
/// <para>
/// Known, documented v1 limitations: a single CJK character does not match inside a longer CJK run,
/// and supplementary-plane ideographs (outside the BMP ranges below) are left to default unicode61
/// behavior. Both are recorded in docs/SEARCH_SPEC.md section 4.2 rather than hidden.
/// </para>
/// </summary>
public static class CjkBigramProjection
{
    /// <summary>
    /// A character is a CJK ideograph iff it is in U+3400–U+4DBF, U+4E00–U+9FFF or U+F900–U+FAFF
    /// (docs/SEARCH_SPEC.md section 4.2, rule 1). Operates on UTF-16 code units.
    /// </summary>
    public static bool IsCjkIdeograph(char character) =>
        character is >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    /// <summary>
    /// Projects <paramref name="input"/> per docs/SEARCH_SPEC.md section 4.2: maximal CJK ideograph
    /// runs of length ≥ 2 become their overlapping bigrams joined by a single space, a CJK run of
    /// length 1 is copied verbatim, a single space separates every CJK↔non-CJK boundary, and all
    /// other characters are copied verbatim. The same input always projects to the same output.
    /// </summary>
    public static string Project(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder(input.Length + 8);
        var lastWasCjk = false;
        var index = 0;
        while (index < input.Length)
        {
            if (!IsCjkIdeograph(input[index]))
            {
                AppendSeparated(builder, input[index], isCjk: false, ref lastWasCjk);
                index++;
                continue;
            }

            var runStart = index;
            while (index < input.Length && IsCjkIdeograph(input[index]))
                index++;

            if (index - runStart == 1)
            {
                AppendSeparated(builder, input[runStart], isCjk: true, ref lastWasCjk);
            }
            else
            {
                // First bigram: boundary-aware first character, then its partner. Consecutive
                // bigrams share one character, so each following bigram is "space + two characters":
                // 大模型上线 → 大模 模型 型上 上线.
                AppendSeparated(builder, input[runStart], isCjk: true, ref lastWasCjk);
                builder.Append(input[runStart + 1]);
                for (var offset = 1; offset < index - runStart - 1; offset++)
                {
                    builder.Append(' ');
                    builder.Append(input[runStart + offset]);
                    builder.Append(input[runStart + offset + 1]);
                }

                lastWasCjk = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends one character, inserting a separating space at every CJK↔non-CJK boundary.
    /// unicode61 treats ASCII letters/digits and CJK ideographs alike as token characters, so
    /// without this separator a projected bigram would glue to an adjacent Latin run (for example
    /// <c>汇报V8</c> or <c>200.00元</c> would tokenize as one token) and break matching at the
    /// boundary. The inserted space is itself a unicode61 separator, so it never merges tokens and
    /// never changes within-class tokenization (docs/SEARCH_SPEC.md section 4.2, rule 3).
    /// </summary>
    private static void AppendSeparated(System.Text.StringBuilder builder, char character, bool isCjk, ref bool lastWasCjk)
    {
        if (builder.Length > 0 && isCjk != lastWasCjk)
            builder.Append(' ');
        builder.Append(character);
        lastWasCjk = isCjk;
    }
}
