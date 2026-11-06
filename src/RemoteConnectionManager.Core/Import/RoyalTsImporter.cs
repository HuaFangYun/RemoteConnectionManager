using System.Xml;
using System.Xml.Linq;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Import;

/// <summary>
/// Reads a Royal TS document (.rtsz, root RTSZDocument). Every object is a direct child of the
/// root and points at its parent through ParentID; the RoyalDocument object is the top of the tree.
/// RoyalRDSConnection becomes an RDP entry, RoyalSSHConnection an SSH or telnet entry,
/// RoyalWebConnection a website entry and RoyalFolder a folder. Anything under the Trashcan
/// is ignored. Passwords (stored encrypted in the file) are never read: user names are taken from
/// the entry itself or from the RoyalCredential it points at.
/// </summary>
public static class RoyalTsImporter
{
    private const string Document = "RoyalDocument";
    private const string Folder = "RoyalFolder";
    private const string Rdp = "RoyalRDSConnection";
    private const string Ssh = "RoyalSSHConnection";
    private const string Web = "RoyalWebConnection";
    private const string Credential = "RoyalCredential";
    private const string Trash = "RoyalTrash";

    public static ImportResult Import(string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                "This file is not a readable Royal TS document. Encrypted documents must be saved without " +
                "encryption in Royal TS first. " + ex.Message, ex);
        }
        return Import(document, System.IO.Path.GetFileNameWithoutExtension(path));
    }

    public static ImportResult Import(XDocument document, string fallbackName = "Royal TS import")
    {
        var rootElement = document.Root ?? throw new InvalidDataException("Empty document.");
        var objects = rootElement.Elements().Where(e => Guid.TryParse(Text(e, "ID"), out _)).ToList();
        var docObject = objects.FirstOrDefault(e => e.Name.LocalName == Document)
            ?? throw new InvalidDataException("No RoyalDocument found. Expected a Royal TS .rtsz document.");

        var byId = objects.GroupBy(e => Text(e, "ID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var children = objects.ToLookup(e => Text(e, "ParentID") ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var credentials = objects.Where(e => e.Name.LocalName == Credential).ToList();
        var credentialsById = credentials.ToDictionary(c => Text(c, "ID")!, StringComparer.OrdinalIgnoreCase);

        var notes = new List<string>();
        var imported = 0;
        var skippedByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var vaultedUsers = 0;
        var missingCredentials = 0;

        var docId = Text(docObject, "ID")!;
        var root = new ConnectionFolder { Name = Text(docObject, "Name") ?? fallbackName };
        ApplyCredential(docObject, root.Defaults);

        // Objects whose parent is missing land at the top rather than vanishing.
        var orphans = objects.Where(e => !ReferenceEquals(e, docObject)
            && (Text(e, "ParentID") is not { } p || !byId.ContainsKey(p))).ToList();

        Fill(root, children[docId].Concat(orphans));

        if (vaultedUsers > 0)
            notes.Add($"{vaultedUsers} entries or folders had a saved Royal TS credential. User names were kept; passwords were not. " +
                "Set a Key Vault credential, or Windows will prompt.");
        if (missingCredentials > 0)
            notes.Add($"{missingCredentials} entries or folders point at a credential that is not in this document (probably another .rtsz). Their user name was left blank.");
        foreach (var (type, count) in skippedByType.OrderByDescending(kv => kv.Value))
            notes.Add($"Skipped {count} {Friendly(type)}.");

        Prune(root);
        return new ImportResult(root, imported, skippedByType.Values.Sum(), notes);

        void Fill(ConnectionFolder target, IEnumerable<XElement> items)
        {
            foreach (var item in items.OrderBy(e => Int(Text(e, "PositionNr")) ?? int.MaxValue))
            {
                switch (item.Name.LocalName)
                {
                    case Document or Trash or Credential:
                        // The trash and its contents are dropped; credentials are only looked up.
                        continue;
                    case Folder:
                    {
                        var folder = new ConnectionFolder
                        {
                            Name = Text(item, "Name") ?? "(unnamed)",
                            Description = Text(item, "Description"),
                        };
                        ApplyCredential(item, folder.Defaults);
                        target.Folders.Add(folder);
                        Fill(folder, children[Text(item, "ID")!]);
                        continue;
                    }
                    case Rdp:
                    {
                        var name = Text(item, "Name") ?? "(unnamed)";
                        var (host, port) = RdmImporter.SplitHostPort(Text(item, "URI"));
                        if (string.IsNullOrWhiteSpace(host))
                        {
                            Count("RDP connections without a host");
                            continue;
                        }

                        var rdp = new RdpConnection { Name = name, Host = host, Description = Text(item, "Description") };
                        port ??= Int(Text(item, "RDPPort"));
                        if (port is > 0 and not EffectiveRdpSettings.DefaultPort) rdp.Port = port;
                        if (RdmImporter.BoolOrNull(Text(item, "ConnectToAdministerOrConsole")) == true) rdp.AdminSession = true;
                        if (RdmImporter.BoolOrNull(Text(item, "SmartSizing")) == true) rdp.SmartSizing = true;
                        if (RdmImporter.BoolOrNull(Text(item, "RedirectClipboard")) == false) rdp.RedirectClipboard = false;
                        if (RdmImporter.BoolOrNull(Text(item, "EnableWindowsKey")) == false) rdp.KeyboardPassThrough = false;
                        if (RdmImporter.BoolOrNull(Text(item, "RestrictedAdminMode")) == true) rdp.CredentialProtection = CredentialProtection.RestrictedAdmin;

                        var defaults = new ConnectionDefaults();
                        ApplyCredential(item, defaults);
                        rdp.Domain = defaults.Domain;
                        rdp.UserName = defaults.UserName;

                        if (Text(item, "RDSGatewayID") is { } gw && Guid.TryParse(gw, out var g) && g != Guid.Empty)
                            notes.Add($"'{name}': uses a Royal TS gateway object, which was not imported. Set the gateway on the entry.");

                        target.Connections.Add(rdp);
                        imported++;
                        continue;
                    }
                    case Ssh:
                    {
                        var name = Text(item, "Name") ?? "(unnamed)";
                        if (RdmImporter.BoolOrNull(Text(item, "IsSerialPortConnection")) == true)
                        {
                            Count("serial port connections");
                            continue;
                        }
                        var (host, port) = RdmImporter.SplitHostPort(Text(item, "URI"));
                        if (string.IsNullOrWhiteSpace(host))
                        {
                            Count("SSH connections without a host");
                            continue;
                        }

                        var ssh = new SshConnection
                        {
                            Name = name,
                            Host = host,
                            Description = Text(item, "Description"),
                            Protocol = RdmImporter.BoolOrNull(Text(item, "IsTelnetConnection")) == true ? TerminalProtocol.Telnet : TerminalProtocol.Ssh,
                        };
                        port ??= Int(Text(item, "Port"));
                        if (port is > 0 && port != ssh.EffectivePort) ssh.Port = port;
                        // Royal TS has a switch per algorithm family; RCM has one for the lot.
                        ssh.LegacyAlgorithms = new[] { "Encryption", "HostKey", "KeyExchange", "Mac" }
                            .Any(f => RdmImporter.BoolOrNull(Text(item, $"AllowInsecureSsh{f}Algorithms")) == true);

                        var defaults = new ConnectionDefaults();
                        ApplyCredential(item, defaults);
                        if (defaults.UserName is { } user)
                            ssh.UserName = defaults.Domain is { } domain ? $@"{domain}\{user}" : user;

                        target.Connections.Add(ssh);
                        imported++;
                        continue;
                    }
                    case Web:
                    {
                        var name = Text(item, "Name") ?? "(unnamed)";
                        var url = Text(item, "URI");
                        if (WebConnection.Normalise(url) is null)
                        {
                            Count("web pages without a usable address");
                            continue;
                        }
                        target.Connections.Add(new WebConnection { Name = name, Url = url!, Description = Text(item, "Description") });
                        imported++;
                        continue;
                    }
                    default:
                        Count(item.Name.LocalName);
                        Fill(target, children[Text(item, "ID")!]);
                        continue;
                }
            }
        }

        void Count(string type) => skippedByType[type] = skippedByType.GetValueOrDefault(type) + 1;

        // Royal TS CredentialMode: 2 = user name on the object, 3 = credential object by ID,
        // 4 = credential object by name. CredentialFromParent, 0 and the rest leave it to inherit.
        void ApplyCredential(XElement item, ConnectionDefaults target)
        {
            if (RdmImporter.BoolOrNull(Text(item, "CredentialFromParent")) == true) return;

            string? user = null;
            switch (Text(item, "CredentialMode"))
            {
                case "2":
                    user = Text(item, "CredentialUsername");
                    break;
                case "3":
                    if (Text(item, "CredentialId") is { } id && credentialsById.TryGetValue(id, out var byIdCredential))
                        user = Text(byIdCredential, "UserName");
                    else missingCredentials++;
                    break;
                case "4":
                    var credentialName = Text(item, "CredentialName");
                    var byName = credentials.FirstOrDefault(c => string.Equals(Text(c, "Name"), credentialName, StringComparison.OrdinalIgnoreCase));
                    if (byName is not null) user = Text(byName, "UserName");
                    else missingCredentials++;
                    break;
            }

            if (user is null) return;
            (target.Domain, target.UserName) = RdmCsvImporter.SplitUser(user, null);
            vaultedUsers++;
        }
    }

    /// <summary>Drops folders left empty, for example ones that only held credentials or web links.</summary>
    private static bool Prune(ConnectionFolder folder)
    {
        folder.Folders.RemoveAll(f => !Prune(f));
        return folder.Folders.Count > 0 || folder.Connections.Count > 0;
    }

    private static string Friendly(string type) => type switch
    {
        "RoyalWebConnection" => "web pages",
        "RoyalSSHConnection" => "SSH connections",
        "RoyalVNCConnection" => "VNC connections",
        "RoyalTeamViewerConnection" => "TeamViewer connections",
        _ when type.StartsWith("Royal", StringComparison.Ordinal) => type["Royal".Length..] + " objects",
        _ => type,
    };

    private static string? Text(XElement element, string name)
    {
        var value = element.Element(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? Int(string? value) => RdmImporter.IntOrNull(value);
}
