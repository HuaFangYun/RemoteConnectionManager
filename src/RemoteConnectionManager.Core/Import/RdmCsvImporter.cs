using System.Text;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Core.Import;

/// <summary>
/// Reads a Remote Desktop Manager CSV export of RDP entries. RDM writes one row per entry with
/// Name, Description, URI (host, sometimes "host:port"), RDPPort, Path ("Parent/Child") and the
/// RDP tab settings. Password columns are never read. RDM's other CSV layout (Name, Description
/// and custom fields only) has no host or folder, so it is rejected with a pointer to the right export.
/// </summary>
public static class RdmCsvImporter
{
    public static ImportResult Import(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Import(reader, System.IO.Path.GetFileNameWithoutExtension(path));
    }

    public static ImportResult Import(TextReader reader, string rootName = "RDM import")
    {
        var rows = ReadCsv(reader);
        if (rows.Count == 0) throw new InvalidDataException("The CSV file is empty.");

        var header = rows[0];
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++) columns.TryAdd(header[i].Trim(), i);

        int? Column(params string[] names)
        {
            foreach (var name in names)
                if (columns.TryGetValue(name, out var index)) return index;
            return null;
        }

        var hostColumn = Column("URI", "Host", "Url", "ComputerName");
        if (hostColumn is null || Column("Name") is null)
            throw new InvalidDataException(
                "This CSV has no host column (URI). In Remote Desktop Manager export the RDP entries to CSV with their " +
                "RDP settings, or use File, Export, Export entries (.rdm) instead.");

        var notes = new List<string>();
        var imported = 0;
        var skipped = 0;
        var savedCredentials = 0;
        var root = new ConnectionFolder { Name = rootName };
        var folders = new Dictionary<string, ConnectionFolder>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };

        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            string? Get(params string[] names)
            {
                var index = Column(names);
                if (index is null || index >= row.Count) return null;
                var value = row[index.Value];
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            var name = Get("Name") ?? "(unnamed)";
            var (host, port) = RdmImporter.SplitHostPort(Get("URI", "Host", "Url", "ComputerName"));
            if (string.IsNullOrWhiteSpace(host))
            {
                skipped++;
                notes.Add($"Skipped '{name}': no host.");
                continue;
            }

            var rdp = new RdpConnection
            {
                Name = name,
                Host = host,
                Description = Get("Description"),
            };

            port ??= RdmImporter.IntOrNull(Get("RDPPort", "Port"));
            if (port is > 0 and not EffectiveRdpSettings.DefaultPort) rdp.Port = port;

            if (RdmImporter.BoolOrNull(Get("ConnectToAdministerOrConsole", "AdminMode", "Console")) == true) rdp.AdminSession = true;
            if (RdmImporter.BoolOrNull(Get("SmartSizing")) == true) rdp.SmartSizing = true;
            if (RdmImporter.BoolOrNull(Get("RedirectClipboard")) == false) rdp.RedirectClipboard = false;
            if (RdmImporter.BoolOrNull(Get("EnableWindowsKey")) == false) rdp.KeyboardPassThrough = false;

            (rdp.Domain, rdp.UserName) = SplitUser(Get("CredentialUsername", "UserName", "Username"), Get("CredentialDomain", "Domain"));

            if (Get("GatewayHostName", "GatewayHostname", "GatewayHost") is { } gateway)
            {
                rdp.GatewayHost = gateway;
                rdp.GatewayUsage = RdmImporter.GatewayUsageOrNull(Get("GatewayUsageMethod"));
                if (rdp.GatewayUsage is null)
                    notes.Add($"'{name}': gateway {gateway} imported, check its usage setting.");
            }

            // Anything other than 0 means RDM supplied a credential (saved, linked or prompted).
            if (Get("CredentialMode") is { } mode && mode != "0") savedCredentials++;

            RdmImporter.GetFolder(folders, RdmImporter.NormalisePath(Get("Path", "Group", "Folder"))).Connections.Add(rdp);
            imported++;
        }

        if (savedCredentials > 0)
            notes.Insert(0, $"{savedCredentials} entries used an RDM credential. User names were kept where present; passwords were not. " +
                "Set a Key Vault credential, or Windows will prompt.");

        return new ImportResult(root, imported, skipped, notes);
    }

    /// <summary>
    /// "DOMAIN\user" becomes Domain + UserName. ".\user" (local account) stays whole, which the
    /// RDP control accepts as is. "user@domain" stays whole too.
    /// </summary>
    public static (string? Domain, string? UserName) SplitUser(string? user, string? domain)
    {
        if (user is null) return (domain, null);
        var cut = user.IndexOf('\\');
        if (domain is null && cut > 0 && user[..cut] != ".")
            return (user[..cut], user[(cut + 1)..]);
        return (domain, user);
    }

    /// <summary>RFC 4180 reader: quoted fields may hold commas, doubled quotes and line breaks.</summary>
    public static List<List<string>> ReadCsv(TextReader reader)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (fieldStarted || field.Length > 0 || row.Count > 0)
                    {
                        row.Add(field.ToString());
                        rows.Add(row);
                    }
                    row = new List<string>();
                    field.Clear();
                    fieldStarted = false;
                    break;
                default:
                    field.Append(ch);
                    fieldStarted = true;
                    break;
            }
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
