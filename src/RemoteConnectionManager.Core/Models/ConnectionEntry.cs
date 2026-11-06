using System.Text.Json.Serialization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>
/// Base for every leaf in the navigation tree. The JSON "type" discriminator picks the
/// concrete class: "rdp", "terminal", "keyvault", "ssh" or "web". Never stores a password.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RdpConnection), "rdp")]
[JsonDerivedType(typeof(TerminalConnection), "terminal")]
[JsonDerivedType(typeof(KeyVaultConnection), "keyvault")]
[JsonDerivedType(typeof(SshConnection), "ssh")]
[JsonDerivedType(typeof(WebConnection), "web")]
public abstract class ConnectionEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Favourite { get; set; }
    /// <summary>Icon colour as "#RRGGBB". Null inherits from the folders above, then the type's own colour.</summary>
    public string? IconColour { get; set; }

    [JsonIgnore]
    public abstract ConnectionKind Kind { get; }

    /// <summary>Short protocol prefix shown in the tree, for example "(RDP)". Overrides must repeat [JsonIgnore]: System.Text.Json does not inherit it.</summary>
    [JsonIgnore]
    public abstract string Prefix { get; }

    public abstract ConnectionEntry Clone();

    protected void CopyBaseTo(ConnectionEntry target)
    {
        target.Id = Guid.NewGuid();
        target.Name = Name;
        target.Description = Description;
        target.Tags = new List<string>(Tags);
        target.Favourite = Favourite;
        target.IconColour = IconColour;
    }
}

public enum ConnectionKind
{
    Rdp,
    Terminal,
    KeyVault,
    /// <summary>SSH or telnet.</summary>
    Ssh,
    Web,
}
