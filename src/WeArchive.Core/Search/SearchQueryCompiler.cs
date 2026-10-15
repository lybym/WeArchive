using WeArchive.Core.Query;

namespace WeArchive.Core.Search;

/// <summary>One parsed query term with its grammar markers (docs/SEARCH_SPEC.md section 5.1).</summary>
/// <param name="Content">The term with its surrounding quotes / trailing prefix star removed.</param>
/// <param name="IsPhrase">True when the term was wrapped in double quotes.</param>
/// <param name="HasPrefixStar">True when the term carried one trailing <c>*</c> prefix marker.</param>
public readonly record struct SearchQueryTerm(string Content, bool IsPhrase, bool HasPrefixStar);

/// <summary>
/// Parses and compiles caller query text into the FTS5 MATCH expression of the matching contract
/// (docs/SEARCH_SPEC.md sections 5.1 and 5.2).
/// <para>
/// A query is a sequence of whitespace-separated terms; a term wrapped in double quotes is one
/// phrase term (quotes allow spaces inside; an unbalanced quote is an ordinary character of the
/// term it appeared in); one trailing <c>*</c> marks a prefix term; terms combine with implicit
/// AND. Every term is projected with <see cref="CjkBigramProjection"/> exactly like the indexed
/// text and then quoted with embedded quotes doubled, so caller input can never become FTS5 query
/// syntax — an input like <c>模型") OR (1=1) --</c> is one literal token sequence that matches
/// nothing.
/// </para>
/// <para>
/// A query with no terms, or a term without any letter, digit or CJK ideograph, is a deterministic
/// usage failure (<c>query_invalid</c>); validation happens here, before any FTS5 call, and the
/// failure message never echoes the query content (docs/SEARCH_SPEC.md section 5.3).
/// </para>
/// </summary>
public static class SearchQueryCompiler
{
    /// <summary>
    /// Parses the caller query into its term structure.
    /// </summary>
    /// <exception cref="ArchiveQueryException">
    /// The query contains no terms, or a term contains no letter, digit or CJK ideograph
    /// (<c>query_invalid</c>).
    /// </exception>
    public static IReadOnlyList<SearchQueryTerm> Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rawTokens = SplitTokens(query);
        if (rawTokens.Count == 0)
            throw ArchiveQueryException.QueryInvalid("the query is empty.");

        var terms = new List<SearchQueryTerm>(rawTokens.Count);
        foreach (var raw in rawTokens)
        {
            var content = raw;
            var isPhrase = false;
            if (content.Length >= 2 && content.StartsWith('"') && content.EndsWith('"'))
            {
                isPhrase = true;
                content = content[1..^1];
            }

            var hasPrefixStar = false;
            if (content.EndsWith('*'))
            {
                hasPrefixStar = true;
                content = content[..^1];
            }

            if (content.Length == 0)
                throw ArchiveQueryException.QueryInvalid("the query contains an empty term.");

            if (!content.Any(character => char.IsLetterOrDigit(character) || CjkBigramProjection.IsCjkIdeograph(character)))
                throw ArchiveQueryException.QueryInvalid("the query contains a term without any searchable character.");

            terms.Add(new SearchQueryTerm(content, isPhrase, hasPrefixStar));
        }

        return terms;
    }

    /// <summary>
    /// Projects every term and compiles the FTS5 MATCH expression: quoted projected tokens joined
    /// by spaces (implicit AND), with a trailing <c>*</c> for prefix terms. The projection matches
    /// the indexed text exactly (docs/SEARCH_SPEC.md section 5.2).
    /// </summary>
    public static string Compile(IReadOnlyList<SearchQueryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0)
            throw ArchiveQueryException.QueryInvalid("the query is empty.");

        return string.Join(' ', terms.Select(term => Quote(CjkBigramProjection.Project(term.Content)) + (term.HasPrefixStar ? "*" : string.Empty)));
    }

    /// <summary>
    /// Parses and compiles in one step: the caller query as written.
    /// </summary>
    public static string CompileQuery(string query) => Compile(Parse(query));

    /// <summary>Wraps content in FTS5 phrase quotes, doubling embedded quotes.</summary>
    public static string Quote(string content) =>
        "\"" + content.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Splits the query on whitespace outside double quotes. A quote toggles in-quote state until
    /// its closing quote or the end of the query, so an unbalanced quote stays an ordinary
    /// character of the term it appeared in (docs/SEARCH_SPEC.md section 5.1).
    /// </summary>
    private static List<string> SplitTokens(string query)
    {
        var tokens = new List<string>();
        var builder = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var character in query)
        {
            if (character == '"')
            {
                inQuote = !inQuote;
                builder.Append(character);
            }
            else if (char.IsWhiteSpace(character) && !inQuote)
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }
            }
            else
            {
                builder.Append(character);
            }
        }

        if (builder.Length > 0)
            tokens.Add(builder.ToString());

        return tokens;
    }
}
