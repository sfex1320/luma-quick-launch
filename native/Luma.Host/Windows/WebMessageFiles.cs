using Microsoft.Web.WebView2.Core;
namespace Luma.Host.Windows;

public static class WebMessageFiles
{
    // Ordinary postMessage has no AdditionalObjects collection on some WebView2 runtimes.
    public static string[] ExtractPaths(IEnumerable<object>? objects) => objects?
        .OfType<CoreWebView2File>().Select(file => file.Path).ToArray() ?? Array.Empty<string>();
}
