using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TwinCatGateway.Mcp;

public static class McpReferenceGenerator
{
    public const string StartMarker = "<!-- BEGIN GENERATED MCP CATALOG -->";
    public const string EndMarker = "<!-- END GENERATED MCP CATALOG -->";

    public static string GenerateCatalogSection()
    {
        StringBuilder builder = new();
        builder.AppendLine(StartMarker);
        builder.AppendLine("## 8. Generated MCP catalog");
        builder.AppendLine();
        builder.AppendLine("This section is generated from `GatewayMcpCatalog`; do not edit it by hand.");
        builder.AppendLine();
        builder.AppendLine("### 8.1 Tools");
        builder.AppendLine();
        builder.AppendLine("| Name | Input schema | Output schema | Capability | Annotations |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (McpToolDefinition tool in GatewayMcpCatalog.Tools.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append("| `").Append(tool.Name).Append("` | `")
                .Append(Escape(tool.InputSchema)).Append("` | `")
                .Append(Escape(FormatType(tool.OutputSchemaType))).Append("` | `")
                .Append(Escape(tool.Capability)).Append("` | `")
                .Append("readOnly=").Append(Lower(tool.ReadOnly))
                .Append(", destructive=").Append(Lower(tool.Destructive))
                .Append(", idempotent=").Append(Lower(tool.Idempotent))
                .Append(", openWorld=").Append(Lower(tool.OpenWorld))
                .AppendLine("` |");
        }

        builder.AppendLine();
        builder.AppendLine("### 8.2 Resource templates");
        builder.AppendLine();
        builder.AppendLine("| URI template | Name | MIME type |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (McpResourceDefinition resource in GatewayMcpCatalog.Resources.OrderBy(item => item.UriTemplate, StringComparer.Ordinal))
        {
            builder.Append("| `").Append(resource.UriTemplate).Append("` | ")
                .Append(resource.Name).Append(" | `")
                .Append(resource.MimeType).AppendLine("` |");
        }
        builder.AppendLine(EndMarker);
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static string UpdateReference(string current)
    {
        ArgumentNullException.ThrowIfNull(current);
        int start = current.IndexOf(StartMarker, StringComparison.Ordinal);
        int end = current.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("MCP reference generated catalog markers are missing or out of order.");
        }

        end += EndMarker.Length;
        return current[..start] + GenerateCatalogSection().TrimEnd('\r', '\n') + current[end..];
    }

    public static bool IsCurrent(string reference) =>
        string.Equals(reference, UpdateReference(reference), StringComparison.Ordinal);

    public static void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string current = File.ReadAllText(path);
        File.WriteAllText(path, UpdateReference(current), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string FormatType(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`')];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatType)) + ">";
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Lower(bool value) => value ? "true" : "false";
}
