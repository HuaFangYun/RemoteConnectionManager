using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Import;

/// <summary>
/// Reads a Devolutions Remote Desktop Manager XML export (.rdm, root ArrayOfConnection) and
/// produces a folder tree of RDP entries. Passwords and credential entries are ignored on
/// purpose. Non RDP entry types are skipped and counted.
/// </summary>
/// <remarks>
/// The .rdm layout is not formally documented, so element names are matched case insensitively
/// and several known spellings are tried. Folders come from each entry's Group path
/// ("Parent\Child"); Group type entries only add the description. Settings that belong to the
/// RDP tab live in a child RDP element on most versions, so that is checked before the entry.
/// </remarks>
public static class RdmImporter
{
    private static readonly char[] PathSeparators = { '\\', '/' };

    /// <summary>Reads an .rdm/.xml export, or hands a .csv export to <see cref="RdmCsvImporter"/>.</summary>
    public static ImportResult Import(string path)
    {
        if (string.Equals(System.IO.Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            return RdmCsvImporter.Import(path);

        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                "This file is not a readable RDM XML export. In Remote Desktop Manager use File, Export, Export entries (.rdm), " +
                "and do not protect the export with a password. " + ex.Message, ex);
        }
        return Import(document, System.IO.Path.GetFileNameWithoutExtension(path));
    }

    public static ImportResult Import(XDocument document, string rootName = "RDM import")
    {
        var rootElement = document.Root ?? throw new InvalidDataException("Empty document.");

        // Every entry is a Connection element with a ConnectionType child. Searching descendants
        // copes with both the bare ArrayOfConnection root and wrapped exports.
        var entries = rootElement.DescendantsAndSelf()
            .Where(e => Is(e, "Connection") && Child(e, "ConnectionType") is not null)
            .ToList();
        if (entries.Count == 0)
            throw new InvalidDataException("No RDM entries found. Expected an ArrayOfConnection export with Connection elements.");

        var notes = new List<string>();
        var imported = 0;
        var skipped = 0;
        var root = new ConnectionFolder { Name = rootName };
        var folders = new Dictionary<string, ConnectionFolder>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };

        // Folders first so their descriptions land even when they appear after their children.
        foreach (var entry in entries.Where(e => IsType(e, "Group")))
        {
            var name = Text(entry, "Name") ?? "(unnamed)";
            var group = NormalisePath(Text(entry, "Group"));
            // RDM stores a folder's own full path in Group; older exports store the parent path only.
            var fullPath = group.Length == 0
                ? name
                : LastSegment(group).Equals(name, StringComparison.OrdinalIgnoreCase) ? group : group + "\\" + name;
            var folder = GetFolder(folders, fullPath);
            folder.Description ??= Text(entry, "Description");
        }

        foreach (var entry in entries.Where(e => !IsType(e, "Group")))
        {
            var type = Text(entry, "ConnectionType") ?? "unknown";
            var name = Text(entry, "Name") ?? "(unnamed)";

            if (IsType(entry, "SSHShell") || IsType(entry, "Telnet"))
            {
                var terminal = Child(entry, "Terminal");
                var (sshHost, sshPort) = SplitHostPort(Text(entry, "Url") ?? Text(entry, "Host") ?? Text(terminal, "Host"));
                if (string.IsNullOrWhiteSpace(sshHost))
                {
                    skipped++;
                    notes.Add($"Skipped '{name}': no host.");
                    continue;
                }
                var ssh = new SshConnection
                {
                    Name = name,
                    Host = sshHost,
                    Description = Text(entry, "Description"),
                    Protocol = IsType(entry, "Telnet") ? TerminalProtocol.Telnet : TerminalProtocol.Ssh,
                    UserName = Setting(entry, terminal, "Username", "UserName"),
                };
                sshPort ??= IntOrNull(Setting(entry, terminal, "HostPort", "Port"));
                if (sshPort is > 0 && sshPort != ssh.EffectivePort) ssh.Port = sshPort;
                GetFolder(folders, NormalisePath(Text(entry, "Group"))).Connections.Add(ssh);
                imported++;
                continue;
            }

            if (IsType(entry, "WebBrowser"))
            {
                var url = Text(entry, "Url") ?? Setting(entry, Child(entry, "Web"), "Url", "WebBrowserUrl");
                if (WebConnection.Normalise(url) is null)
                {
                    skipped++;
                    notes.Add($"Skipped '{name}': no usable web address.");
                    continue;
                }
                GetFolder(folders, NormalisePath(Text(entry, "Group"))).Connections.Add(
                    new WebConnection { Name = name, Url = url!, Description = Text(entry, "Description") });
                imported++;
                continue;
            }

            if (!IsType(entry, "RDPConfigured"))
            {
                skipped++;
                notes.Add(IsType(entry, "Credential")
                    ? $"Skipped '{name}': credential entries are not imported."
                    : $"Skipped '{name}': type {type} is not supported.");
                continue;
            }

            var (host, port) = SplitHostPort(Text(entry, "Url") ?? Text(entry, "Host"));
            if (string.IsNullOrWhiteSpace(host))
            {
                skipped++;
                notes.Add($"Skipped '{name}': no host.");
                continue;
            }

            var rdpSettings = Child(entry, "RDP");
            var rdp = new RdpConnection
            {
                Name = name,
                Host = host,
                Description = Text(entry, "Description"),
                Domain = Setting(entry, rdpSettings, "Domain"),
                UserName = Setting(entry, rdpSettings, "UserName", "Username"),
            };

            port ??= IntOrNull(Setting(entry, rdpSettings, "Port", "RDPPort"));
            if (port is > 0 and not EffectiveRdpSettings.DefaultPort) rdp.Port = port;

            if (BoolOrNull(Setting(entry, rdpSettings, "Favorite", "IsFavorite")) == true) rdp.Favourite = true;

            // Only carry a true admin session; false is the default and should inherit.
            if (BoolOrNull(Setting(entry, rdpSettings, "AdminMode", "Console", "ConnectToConsole", "UseConsoleSession")) == true)
                rdp.AdminSession = true;

            // Clipboard on is the default, so only an explicit "off" is kept.
            if (BoolOrNull(Setting(entry, rdpSettings, "UsesClipboard", "RedirectClipboard")) == false)
                rdp.RedirectClipboard = false;

            // RDM's KeyboardHook says where Windows key combinations go. Pass through is the default.
            var keyboard = Setting(entry, rdpSettings, "KeyboardHook");
            if (keyboard is not null && keyboard.Contains("Local", StringComparison.OrdinalIgnoreCase))
                rdp.KeyboardPassThrough = false;

            if (BoolOrNull(Setting(entry, rdpSettings, "SmartSizing", "SmartSizingStreched", "SmartSizingStretched")) == true)
                rdp.SmartSizing = true;

            var gateway = Setting(entry, rdpSettings, "GatewayHostname", "GatewayHostName", "GatewayHost");
            if (gateway is not null)
            {
                rdp.GatewayHost = gateway;
                rdp.GatewayUsage = GatewayUsageOrNull(Setting(entry, rdpSettings, "GatewayUsageMethod", "GatewayUsage"));
                if (rdp.GatewayUsage is null)
                    notes.Add($"'{name}': gateway {gateway} imported, check its usage setting.");
            }

            var keywords = Setting(entry, Child(entry, "MetaInformation"), "Keywords", "Tags");
            if (keywords is not null)
                rdp.Tags = keywords.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.Trim('"'))
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (Text(entry, "CredentialConnectionID") is { } credentialId && Guid.TryParse(credentialId, out var g) && g != Guid.Empty)
                notes.Add($"'{name}': linked RDM credential not imported. Set a Key Vault credential or sign in as yourself.");

            GetFolder(folders, NormalisePath(Text(entry, "Group"))).Connections.Add(rdp);
            imported++;
        }

        return new ImportResult(root, imported, skipped, notes);
    }

    internal static ConnectionFolder GetFolder(Dictionary<string, ConnectionFolder> folders, string path)
    {
        if (folders.TryGetValue(path, out var existing)) return existing;
        var cut = path.LastIndexOf('\\');
        var parent = GetFolder(folders, cut < 0 ? string.Empty : path[..cut]);
        var folder = new ConnectionFolder { Name = cut < 0 ? path : path[(cut + 1)..] };
        parent.Folders.Add(folder);
        folders[path] = folder;
        return folder;
    }

    internal static string NormalisePath(string? group) =>
        group is null
            ? string.Empty
            : string.Join("\\", group.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string LastSegment(string path)
    {
        var cut = path.LastIndexOf('\\');
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>Splits "host:port". Leaves bare IPv6 addresses alone and unwraps "[v6]:port".</summary>
    internal static (string? Host, int? Port) SplitHostPort(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, null);
        url = url.Trim();
        if (url.StartsWith('['))
        {
            var close = url.IndexOf(']');
            if (close > 0)
            {
                var v6 = url[1..close];
                var rest = url[(close + 1)..];
                return (v6, rest.StartsWith(':') ? IntOrNull(rest[1..]) : null);
            }
        }
        var colon = url.LastIndexOf(':');
        if (colon > 0 && url.IndexOf(':') == colon && IntOrNull(url[(colon + 1)..]) is { } port)
            return (url[..colon], port);
        return (url, null);
    }

    internal static GatewayUsage? GatewayUsageOrNull(string? value)
    {
        if (value is null) return null;
        if (value.Contains("Always", StringComparison.OrdinalIgnoreCase) || value == "1") return GatewayUsage.Always;
        if (value.Contains("Detect", StringComparison.OrdinalIgnoreCase) || value.Contains("Bypass", StringComparison.OrdinalIgnoreCase) || value == "2")
            return GatewayUsage.BypassLocal;
        if (value.Contains("Direct", StringComparison.OrdinalIgnoreCase) || value.Contains("Never", StringComparison.OrdinalIgnoreCase) || value == "0")
            return GatewayUsage.Never;
        return null;
    }

    private static bool Is(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsType(XElement entry, string type) =>
        string.Equals(Text(entry, "ConnectionType"), type, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => Is(e, name));

    private static string? Text(XElement? parent, string name)
    {
        var child = Child(parent, name);
        if (child is null || child.HasElements) return null;
        var value = child.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>First non empty value from the settings element, then the entry itself.</summary>
    private static string? Setting(XElement entry, XElement? settings, params string[] names)
    {
        foreach (var source in new[] { settings, entry })
            foreach (var name in names)
                if (Text(source, name) is { } value)
                    return value;
        return null;
    }

    internal static bool? BoolOrNull(string? value) => bool.TryParse(value, out var b) ? b : null;

    internal static int? IntOrNull(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
