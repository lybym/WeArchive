using System.Text;
using System.Text.Json.Nodes;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Search;

/// <summary>
/// Builds the normalized index input of <c>docs/SEARCH_SPEC.md</c> sections 3 and 4.1.
/// <para>
/// One FTS row exists per canonical message and its input is that message's own canonical data
/// only: <c>semantic_text</c> plus the selected structured payload fields of the section 3
/// inventory, appended one per line in the listed field order. Absent fields contribute nothing
/// and emit no separator. The construction is a pure function of canonical row content, so the
/// same rows always produce a byte-identical searchable text — that is what makes the derived
/// index deterministically rebuildable.
/// </para>
/// <para>
/// Excluded by default (docs/SEARCH_SPEC.md section 3): provenance and raw-only artifacts,
/// structured measurements, mutable names (participant remarks, conversation titles,
/// forward-bundle sender names, the reply snapshot text) and every field not listed below. The
/// reply snapshot is excluded because it duplicates the target message's text whenever the target
/// is archived; indexing it later is an additive contract change that requires its own
/// documentation.
/// </para>
/// <para>
/// This type never mutates anything and never touches canonical storage; the projection of the
/// built text into index tokens is <see cref="CjkBigramProjection.Project"/>.
/// </para>
/// </summary>
public static class SearchableMessageText
{
    /// <summary>
    /// The combined searchable text of one canonical message:
    /// <c>semantic_text</c>, then one line per indexed payload field in the section 3 order.
    /// </summary>
    public static string Build(CanonicalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Build(message.Type, message.Text, message.Payload);
    }

    /// <summary>
    /// The same construction for a canonical message's persisted column values — the shape the
    /// migration backfill and the deterministic index rebuild read from <c>messages</c>. It is the
    /// identical pure function: same rows, byte-identical searchable text.
    /// </summary>
    public static string Build(CanonicalMessageType type, string semanticText, JsonObject? payload)
    {
        ArgumentNullException.ThrowIfNull(semanticText);

        var builder = new StringBuilder();
        builder.Append(semanticText);

        if (payload is null)
        {
            return builder.ToString();
        }

        switch (type)
        {
            case CanonicalMessageType.File:
                AppendField(builder, payload, "filename");
                break;

            case CanonicalMessageType.Link:
            case CanonicalMessageType.AppShare:
                AppendField(builder, payload, "title");
                AppendField(builder, payload, "description");
                AppendField(builder, payload, "source_app");
                break;

            case CanonicalMessageType.MiniProgram:
                AppendField(builder, payload, "app_name");
                AppendField(builder, payload, "title");
                break;

            case CanonicalMessageType.Location:
                AppendField(builder, payload, "label");
                AppendField(builder, payload, "address");
                break;

            case CanonicalMessageType.ContactCard:
                AppendField(builder, payload, "display_name");
                break;

            case CanonicalMessageType.ForwardBundle:
                AppendField(builder, payload, "title");
                AppendForwardItemTexts(builder, payload);
                break;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends one payload field as its own line. A field the payload does not carry — or carries
    /// as a non-string JSON value — contributes nothing, matching the "absent fields contribute
    /// nothing" rule of docs/SEARCH_SPEC.md section 4.1.
    /// </summary>
    private static void AppendField(StringBuilder builder, JsonObject payload, string fieldName)
    {
        if (payload.TryGetPropertyValue(fieldName, out var value) && value is JsonValue text && text.TryGetValue(out string? content))
        {
            builder.Append('\n').Append(content);
        }
    }

    /// <summary>
    /// Appends every forward-bundle <c>items[].text</c> value in stored item order, one per line.
    /// Nested <c>items[].sender_name</c> is a mutable name and is never indexed
    /// (docs/SEARCH_SPEC.md section 3).
    /// </summary>
    private static void AppendForwardItemTexts(StringBuilder builder, JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("items", out var items) || items is not JsonArray array)
        {
            return;
        }

        foreach (var item in array)
        {
            if (item is JsonObject entry
                && entry.TryGetPropertyValue("text", out var text)
                && text is JsonValue itemText
                && itemText.TryGetValue(out string? content))
            {
                builder.Append('\n').Append(content);
            }
        }
    }
}
