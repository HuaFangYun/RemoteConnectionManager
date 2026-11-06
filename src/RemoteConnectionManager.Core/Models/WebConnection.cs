using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

public enum WebOpenMode
{
    /// <summary>The Windows default browser, which keeps SSO, saved passwords and extensions.</summary>
    Browser,
    /// <summary>A tab inside RCM (WebView2).</summary>
    Tab,
}

/// <summary>A saved web address, for example a switch, firewall or report server admin page.</summary>
public sealed class WebConnection : ConnectionEntry
{
    public string Url { get; set; } = string.Empty;
    /// <summary>What a double click or Connect does. The right click menu offers both.</summary>
    public WebOpenMode OpenIn { get; set; } = WebOpenMode.Browser;

    [JsonIgnore]
    public override ConnectionKind Kind => ConnectionKind.Web;
    [JsonIgnore]
    public override string Prefix => "(WEB)";

    public override ConnectionEntry Clone()
    {
        var copy = new WebConnection { Url = Url, OpenIn = OpenIn };
        CopyBaseTo(copy);
        return copy;
    }

    /// <summary>
    /// The address as an absolute http or https URI. A bare host or path gets https:// in front.
    /// Null when it cannot be made into one, or uses another scheme (file:, javascript: and so on).
    /// </summary>
    public static Uri? Normalise(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return string.IsNullOrEmpty(uri.Host) ? null : uri;
    }
}
