using System.ComponentModel;
using System.Windows.Forms;
using AxMSTSCLib;
using MSTSCLib;
using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Rdp;

/// <summary>
/// WinForms control that owns one Microsoft RDP ActiveX instance. Hosted in WPF through a
/// WindowsFormsHost. Plain mstscax through the MsRdpEx interop, hooking off.
///
/// Behaviours: connect as the logged on user (CredSSP, no password stored) or with a one off
/// <see cref="RdpSignIn"/>, live resize via UpdateSessionDisplaySettings with SmartSizing as the
/// opt in fallback, keyboard hook toggle, clipboard redirection, auto reconnect, admin session,
/// RD Gateway, Restricted Admin and Remote Credential Guard. All of these are plain mstscax
/// settings, so the MsRdpEx hooking layer stays off.
/// </summary>
public sealed class RdpSessionControl : UserControl
{
    private readonly AxMsRdpClient9NotSafeForScripting _rdp;
    private readonly System.Windows.Forms.Timer _resizeTimer;
    private EffectiveRdpSettings? _settings;
    private bool _disconnectRequested;
    private bool _keyboardPassThrough;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RdpSessionState State { get; private set; } = RdpSessionState.Idle;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool KeyboardPassThrough => _keyboardPassThrough;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EffectiveRdpSettings? Settings => _settings;

    public event EventHandler? StateChanged;
    public event EventHandler<RdpDisconnectedEventArgs>? Disconnected;
    public event EventHandler<RdpLogEventArgs>? Log;

    public RdpSessionControl()
    {
        _resizeTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyDisplaySize(); };
        _rdp = new AxMsRdpClient9NotSafeForScripting();
        ((ISupportInitialize)_rdp).BeginInit();
        _rdp.Dock = DockStyle.Fill;
        _rdp.Name = "rdp";
        Controls.Add(_rdp);
        ((ISupportInitialize)_rdp).EndInit();

        _rdp.OnConnecting += (_, _) => SetState(RdpSessionState.Connecting);
        _rdp.OnConnected += (_, _) =>
        {
            Emit("Connected");
            SetState(RdpSessionState.Connected);
            // The host may have changed size while connecting (for example an overlay was showing).
            var size = SafeSize(ClientSize);
            if (size.Width != _rdp.DesktopWidth || size.Height != _rdp.DesktopHeight)
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        };
        _rdp.OnLoginComplete += (_, _) => Emit("Login complete");
        _rdp.OnDisconnected += OnRdpDisconnected;
        _rdp.OnLogonError += (_, e) => Emit($"Logon error {e.lError}");
        _rdp.OnFatalError += (_, e) => Emit($"Fatal error {e.errorCode}");
        _rdp.OnWarning += (_, e) => Emit($"Warning {e.warningCode}");
        _rdp.OnRemoteDesktopSizeChange += (_, e) => Emit($"Remote desktop size {e.width}x{e.height}");
        _rdp.OnAutoReconnecting2 += (_, e) => Emit($"Auto reconnecting, attempt {e.attemptCount} of {e.maxAttemptCount}");
        _rdp.OnAutoReconnected += (_, _) => Emit("Auto reconnected");
        // Moving a session between the tab and a window must keep this handle; a new one means the session was lost.
        _rdp.HandleCreated += (_, _) => Emit($"RDP control window created (0x{_rdp.Handle.ToInt64():X})");
        _rdp.HandleDestroyed += (_, _) => Emit("RDP control window destroyed");

        Resize += (_, _) =>
        {
            if (State == RdpSessionState.Connected)
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            }
        };
    }

    /// <param name="signIn">Explicit account for this attempt, or null for Windows sign in. Not kept.</param>
    public void Connect(EffectiveRdpSettings settings, RdpSignIn? signIn = null)
    {
        _settings = settings;
        _disconnectRequested = false;
        _keyboardPassThrough = settings.KeyboardPassThrough;

        var size = SafeSize(ClientSize);
        Emit($"Connecting to {settings.DisplayTarget} at {size.Width}x{size.Height}");

        _rdp.Server = settings.Host;
        _rdp.ColorDepth = 32;
        _rdp.DesktopWidth = size.Width;
        _rdp.DesktopHeight = size.Height;

        var rcg = settings.Protection == CredentialProtection.RemoteCredentialGuard;
        if (signIn is not null && !rcg)
        {
            _rdp.UserName = signIn.UserName;
            _rdp.Domain = signIn.Domain ?? string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(settings.UserName) && !rcg)
        {
            _rdp.UserName = settings.UserName;
            if (!string.IsNullOrWhiteSpace(settings.Domain)) _rdp.Domain = settings.Domain;
        }
        else
        {
            // Logged on user. CredSSP then uses the logon session (Kerberos on an AD joined box).
            _rdp.UserName = Environment.UserName;
            _rdp.Domain = Environment.UserDomainName;
        }

        var adv = _rdp.AdvancedSettings9;
        adv.RDPPort = settings.Port;
        adv.EnableCredSspSupport = true;
        adv.AuthenticationLevel = 2;
        adv.RedirectClipboard = settings.RedirectClipboard;
        adv.RedirectDrives = false;
        adv.RedirectPrinters = false;
        adv.RedirectSmartCards = false;
        adv.EnableAutoReconnect = true;
        adv.MaxReconnectAttempts = 5;
        adv.keepAliveInterval = 60000;
        adv.GrabFocusOnConnect = true;
        adv.SmartSizing = settings.SmartSizing;
        adv.ConnectToAdministerServer = settings.AdminSession;
        adv.BitmapPersistence = 1;
        adv.allowBackgroundInput = 0;
        // Always overwrite so a reconnect without a sign in does not reuse an earlier password.
        adv.ClearTextPassword = signIn is not null && !rcg ? signIn.Password : string.Empty;

        ApplyGateway(settings);
        ApplyProtection(settings.Protection);

        _rdp.SecuredSettings3.KeyboardHookMode = settings.KeyboardPassThrough ? 1 : 0;
        _rdp.SecuredSettings3.AudioRedirectionMode = 2;

        var ns = (IMsRdpClientNonScriptable5)_rdp.GetOcx()!;
        ns.PromptForCredentials = false;
        ns.AllowPromptingForCredentials = true;

        if (signIn is not null) Emit(rcg ? "Remote Credential Guard: ignoring the explicit account" : $"Signing in as {signIn}");
        SetState(RdpSessionState.Connecting);
        _rdp.Connect();
    }

    public void Reconnect(RdpSignIn? signIn = null)
    {
        if (_settings is null) return;
        if (State is RdpSessionState.Connected or RdpSessionState.Connecting) Disconnect();
        Connect(_settings, signIn);
    }

    private void ApplyGateway(EffectiveRdpSettings settings)
    {
        var t = _rdp.TransportSettings2;
        if (!settings.UsesGateway)
        {
            t.GatewayUsageMethod = 0; // TSC_PROXY_MODE_NONE_DIRECT
            t.GatewayProfileUsageMethod = 1;
            return;
        }
        t.GatewayHostname = settings.GatewayHost;
        t.GatewayUsageMethod = settings.Gateway == GatewayUsage.BypassLocal ? 2u : 1u; // DETECT : DIRECT
        t.GatewayProfileUsageMethod = 1; // use the values set here, not the defaults
        t.GatewayCredsSource = 4;        // TSC_PROXY_CREDS_MODE_ANY: Kerberos/NTLM as the user
        t.GatewayCredSharing = 1;        // same account for the gateway and the host
        Emit($"Using RD Gateway {settings.GatewayHost}{(settings.Gateway == GatewayUsage.BypassLocal ? " (bypassed for local addresses)" : "")}");
    }

    private void ApplyProtection(CredentialProtection protection)
    {
        // Set every time: the same control can reconnect after the entry changed.
        SetExtended("RestrictedLogon", protection == CredentialProtection.RestrictedAdmin);
        SetExtended("DisableCredentialsDelegation", protection == CredentialProtection.RemoteCredentialGuard);
        SetExtended("RedirectedAuthentication", protection == CredentialProtection.RemoteCredentialGuard);
        if (protection != CredentialProtection.None)
            Emit(protection == CredentialProtection.RestrictedAdmin ? "Restricted Admin mode on" : "Remote Credential Guard on");
    }

    private void SetExtended(string name, object value)
    {
        try
        {
            var ext = (IMsRdpExtendedSettings)_rdp.GetOcx()!;
            ext.set_Property(name, ref value);
        }
        catch (Exception ex)
        {
            Emit($"Setting {name} failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        _disconnectRequested = true;
        // At exit the window can be gone before Dispose runs; the session ended with it.
        if (!_rdp.IsHandleCreated) return;
        try
        {
            if (_rdp.Connected != 0) _rdp.Disconnect();
        }
        catch (Exception ex)
        {
            Emit($"Disconnect failed: {ex.Message}");
        }
    }

    public void SetKeyboardPassThrough(bool on)
    {
        _keyboardPassThrough = on;
        try
        {
            _rdp.SecuredSettings3.KeyboardHookMode = on ? 1 : 0;
            Emit(on ? "Keyboard: remote" : "Keyboard: local");
        }
        catch (Exception ex)
        {
            Emit($"Keyboard hook change failed: {ex.Message}");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Push the current host size to the session now rather than waiting for the debounce.</summary>
    public void FitNow() => ApplyDisplaySize();

    public void FocusSession()
    {
        if (_rdp.IsHandleCreated) _rdp.Focus();
    }

    private void ApplyDisplaySize()
    {
        if (State != RdpSessionState.Connected || _settings is null || _settings.SmartSizing) return;
        var size = SafeSize(ClientSize);
        try
        {
            _rdp.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, 0, 0, 0, 100, 100);
        }
        catch (Exception ex)
        {
            Emit($"Live resize unsupported ({ex.Message}). Consider SmartSizing for this host.");
        }
    }

    private void OnRdpDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e)
    {
        string description;
        int extended;
        try
        {
            extended = (int)_rdp.ExtendedDisconnectReason;
            description = _rdp.GetErrorDescription((uint)e.discReason, (uint)extended);
        }
        catch
        {
            extended = 0;
            description = string.Empty;
        }
        if (string.IsNullOrWhiteSpace(description))
            description = e.discReason switch
            {
                1 or 2 or 3 => "Disconnected.",
                _ => $"Disconnected (reason {e.discReason}).",
            };

        Emit($"Disconnected: reason {e.discReason}, extended {extended}. {description}");
        SetState(RdpSessionState.Disconnected);
        Disconnected?.Invoke(this, new RdpDisconnectedEventArgs(e.discReason, extended, description, _disconnectRequested));
        _disconnectRequested = false;
    }

    private static System.Drawing.Size SafeSize(System.Drawing.Size s)
    {
        var w = Math.Max(200, s.Width) & ~1;
        var h = Math.Max(200, s.Height) & ~1;
        return new System.Drawing.Size(w, h);
    }

    private void SetState(RdpSessionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Emit(string message) => Log?.Invoke(this, new RdpLogEventArgs(message));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resizeTimer.Dispose();
            Disconnect();
            _rdp.Dispose();
        }
        base.Dispose(disposing);
    }
}
