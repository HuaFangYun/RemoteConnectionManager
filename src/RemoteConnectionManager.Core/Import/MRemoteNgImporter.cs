using System.Globalization;
using System.Xml.Linq;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Import;

public sealed record ImportResult(ConnectionFolder Root, int Imported, int Skipped, IReadOnlyList<string> Notes);

/// <summary>
/// Reads an mRemoteNG confCons.xml and produces a folder tree of RDP, SSH, telnet and website entries.
/// Passwords are ignored on purpose. Other protocols (VNC, rlogin, raw) are skipped and counted.
/// Fully encrypted files (FullFileEncryption="true") cannot be read; export them
/// unencrypted from mRemoteNG first.
/// </summary>
public static class MRemoteNgImporter
{
    public static ImportResult Import(string path) => Import(XDocument.Load(path), System.IO.Path.GetFileNameWithoutExtension(path));

    public static ImportResult Import(XDocument document, string rootName = "mRemoteNG import")
    {
        var rootElement = document.Root ?? throw new InvalidDataException("Empty document.");
        if (Bool(rootElement.Attribute("FullFileEncryption")?.Value))
            throw new InvalidDataException("This confCons.xml uses full file encryption. In mRemoteNG untick 'Encrypt complete connection file', save, then import again.");

        var notes = new List<string>();
        var imported = 0;
        var skipped = 0;
        var root = new ConnectionFolder { Name = rootName };
        Walk(rootElement, root, ref imported, ref skipped, notes);
        return new ImportResult(root, imported, skipped, notes);
    }

    private static void Walk(XElement parent, ConnectionFolder target, ref int imported, ref int skipped, List<string> notes)
    {
        foreach (var node in parent.Elements("Node"))
        {
            var type = node.Attribute("Type")?.Value ?? "Connection";
            var name = node.Attribute("Name")?.Value ?? "(unnamed)";

            if (string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase))
            {
                var folder = new ConnectionFolder
                {
                    Name = name,
                    Description = Text(node, "Descr"),
                };
                // Container level values become folder defaults so children can inherit them.
                if (!Bool(node.Attribute("InheritDomain")?.Value)) folder.Defaults.Domain = Text(node, "Domain");
                if (!Bool(node.Attribute("InheritUsername")?.Value)) folder.Defaults.UserName = Text(node, "Username");
                if (!Bool(node.Attribute("InheritUseConsoleSession")?.Value)) folder.Defaults.AdminSession = BoolOrNull(node, "UseConsoleSession");
                if (!Bool(node.Attribute("InheritRedirectKeys")?.Value)) folder.Defaults.KeyboardPassThrough = BoolOrNull(node, "RedirectKeys");
                if (!Bool(node.Attribute("InheritRedirectClipboard")?.Value)) folder.Defaults.RedirectClipboard = BoolOrNull(node, "RedirectClipboard");
                if (!Bool(node.Attribute("InheritAutomaticResize")?.Value) && BoolOrNull(node, "AutomaticResize") == false) folder.Defaults.SmartSizing = true;

                target.Folders.Add(folder);
                Walk(node, folder, ref imported, ref skipped, notes);
                continue;
            }

            var protocol = (node.Attribute("Protocol")?.Value ?? "RDP").ToUpperInvariant();
            if (protocol is not ("RDP" or "SSH1" or "SSH2" or "TELNET" or "HTTP" or "HTTPS"))
            {
                skipped++;
                notes.Add($"Skipped '{name}': protocol {protocol} is not supported.");
                continue;
            }

            var host = Text(node, "Hostname");
            if (string.IsNullOrWhiteSpace(host))
            {
                skipped++;
                notes.Add($"Skipped '{name}': no hostname.");
                continue;
            }

            if (protocol is "SSH1" or "SSH2" or "TELNET")
            {
                var ssh = new SshConnection
                {
                    Name = name,
                    Host = host,
                    Description = Text(node, "Descr"),
                    Protocol = protocol == "TELNET" ? TerminalProtocol.Telnet : TerminalProtocol.Ssh,
                    Favourite = Bool(node.Attribute("Favorite")?.Value),
                };
                var sshPort = IntOrNull(node, "Port");
                if (sshPort is > 0 && sshPort != ssh.EffectivePort) ssh.Port = sshPort;
                if (!Bool(node.Attribute("InheritUsername")?.Value)) ssh.UserName = Text(node, "Username");
                if (protocol == "SSH1") notes.Add($"'{name}': SSH version 1 is not supported by OpenSSH; imported as SSH 2.");
                target.Connections.Add(ssh);
                imported++;
                continue;
            }

            if (protocol is "HTTP" or "HTTPS")
            {
                var scheme = protocol.ToLowerInvariant();
                var webPort = IntOrNull(node, "Port");
                var defaultPort = scheme == "https" ? 443 : 80;
                var url = host.Contains("://", StringComparison.Ordinal)
                    ? host
                    : $"{scheme}://{host}{(webPort is > 0 && webPort != defaultPort ? $":{webPort}" : string.Empty)}";
                target.Connections.Add(new WebConnection
                {
                    Name = name,
                    Url = url,
                    Description = Text(node, "Descr"),
                    Favourite = Bool(node.Attribute("Favorite")?.Value),
                });
                imported++;
                continue;
            }

            var rdp = new RdpConnection
            {
                Name = name,
                Host = host,
                Description = Text(node, "Descr"),
            };
            if (Bool(node.Attribute("Favorite")?.Value)) rdp.Favourite = true;

            if (!Bool(node.Attribute("InheritPort")?.Value))
            {
                var port = IntOrNull(node, "Port");
                if (port is > 0 and not EffectiveRdpSettings.DefaultPort) rdp.Port = port;
            }
            if (!Bool(node.Attribute("InheritDomain")?.Value)) rdp.Domain = Text(node, "Domain");
            if (!Bool(node.Attribute("InheritUsername")?.Value)) rdp.UserName = Text(node, "Username");
            if (!Bool(node.Attribute("InheritUseConsoleSession")?.Value)) rdp.AdminSession = BoolOrNull(node, "UseConsoleSession");
            if (!Bool(node.Attribute("InheritRedirectKeys")?.Value)) rdp.KeyboardPassThrough = BoolOrNull(node, "RedirectKeys");
            if (!Bool(node.Attribute("InheritRedirectClipboard")?.Value)) rdp.RedirectClipboard = BoolOrNull(node, "RedirectClipboard");
            if (!Bool(node.Attribute("InheritAutomaticResize")?.Value) && BoolOrNull(node, "AutomaticResize") == false) rdp.SmartSizing = true;

            var tags = Text(node, "EnvironmentTags");
            if (!string.IsNullOrWhiteSpace(tags))
                rdp.Tags = tags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            // Drop no-op values so the entry inherits cleanly.
            if (rdp.AdminSession == false) rdp.AdminSession = null;
            if (rdp.KeyboardPassThrough == true) rdp.KeyboardPassThrough = null;
            if (rdp.RedirectClipboard == true) rdp.RedirectClipboard = null;

            target.Connections.Add(rdp);
            imported++;
        }
    }

    private static string? Text(XElement node, string attribute)
    {
        var value = node.Attribute(attribute)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool Bool(string? value) => bool.TryParse(value, out var b) && b;

    private static bool? BoolOrNull(XElement node, string attribute) =>
        bool.TryParse(node.Attribute(attribute)?.Value, out var b) ? b : null;

    private static int? IntOrNull(XElement node, string attribute) =>
        int.TryParse(node.Attribute(attribute)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
