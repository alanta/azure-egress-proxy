using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Portal.Components;

/// <summary>
/// The glass card, with its header. A tag helper rather than a partial because a card wraps
/// content, and a partial cannot take child content — this is the one component in the set that
/// needs it.
///
/// <code>
/// &lt;console-card eyebrow="Policy" title="Posture" icon="📋"&gt;
///     …
/// &lt;/console-card&gt;
/// </code>
///
/// The treatment — 22px radius, translucent gradient, backdrop blur — is ZEP's
/// <c>GlassCard</c>; the 9px <c>0.14em</c> uppercase mono eyebrow above a 12.5px title is its
/// <c>CardHeader</c>. Both are inherited rather than invented, and the CSS carries them.
/// </summary>
[HtmlTargetElement("console-card")]
public sealed class CardTagHelper : TagHelper
{
    /// <summary>The mono micro-label above the title — the category, not the panel name.</summary>
    public string? Eyebrow { get; set; }

    public string? Title { get; set; }

    /// <summary>A single emoji, matching the mockups. Decorative, so it is hidden from
    /// assistive technology — the eyebrow and title carry the meaning.</summary>
    public string? Icon { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var content = await output.GetChildContentAsync();

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "card");

        if (Eyebrow is not null || Title is not null)
        {
            var head = output.PreContent.AppendHtml("<div class=\"cardhead\">");

            if (Icon is not null)
            {
                head.AppendHtml("<div class=\"ico\" aria-hidden=\"true\">").Append(Icon).AppendHtml("</div>");
            }

            head.AppendHtml("<div>");

            if (Eyebrow is not null)
            {
                head.AppendHtml("<div class=\"eyebrow\">").Append(Eyebrow).AppendHtml("</div>");
            }

            if (Title is not null)
            {
                head.AppendHtml("<div class=\"title\">").Append(Title).AppendHtml("</div>");
            }

            head.AppendHtml("</div></div>");
        }

        output.Content.SetHtmlContent(content);
    }
}
