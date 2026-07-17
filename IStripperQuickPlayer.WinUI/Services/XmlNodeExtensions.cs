using System.Xml;

namespace IStripperQuickPlayer.WinUI.Services;

internal static class XmlNodeExtensions
{
    internal static string GetAttribute(this XmlNode? node, string attributeName)
    {
        if (node?.Attributes == null || string.IsNullOrEmpty(attributeName))
        {
            return string.Empty;
        }

        return node.Attributes[attributeName]?.Value ?? string.Empty;
    }
}
