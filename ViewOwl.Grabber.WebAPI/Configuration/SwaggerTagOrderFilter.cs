using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ViewOwl.Grabber.WebAPI.Configuration;

/// <summary>
/// Reorders Swagger tag groups so that high-priority controllers appear first.
/// Controllers listed in <see cref="PriorityTags"/> are pinned to the top in the
/// specified order; all remaining controllers follow sorted alphabetically.
/// </summary>
public sealed class SwaggerTagOrderFilter : IDocumentFilter
{
    // Tags listed here appear first, in this exact order.
    private static readonly string[] PriorityTags = ["Auth"];

    /// <inheritdoc />
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // Swashbuckle 6.x does not populate swaggerDoc.Tags automatically —
        // collect all tag names from the path operations instead.
        var allTagNames = swaggerDoc.Paths.Values
            .SelectMany(p => p.Operations.Values)
            .SelectMany(o => o.Tags)
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name =>
            {
                int index = Array.IndexOf(PriorityTags, name);
                return index >= 0 ? index : int.MaxValue;
            })
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);

        swaggerDoc.Tags = [.. allTagNames.Select(name => new OpenApiTag { Name = name })];
    }
}
