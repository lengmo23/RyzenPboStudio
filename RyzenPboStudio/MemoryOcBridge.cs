using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using ZenStates.Core;

namespace RyzenPboStudio;

// CONTRACT LOCK NOTICE:
// The prebuilt closed-source MemOcNative.dll embeds an encrypted managed module compiled against
// the exact CLR type identities, member signatures, and handshake salts defined in this file
// (IMemoryOcButton, IMemoryOcHost, IMemoryOcPage, IMemoryOcEntry, and MemoryOcLoader constants).
// Do NOT rename, reorder, remove, or change the signatures of these interfaces/salts when
// modifying the open-source host, or MemOcNative.dll will fail verification and fall back to unavailable.

/// <summary>Minimal host-provided button abstraction for the closed-source Memory OC module.</summary>
public interface IMemoryOcButton
{
    Control AsControl { get; }
    string Text { get; set; }
    Font Font { get; set; }
    Size Size { get; set; }
    Padding Margin { get; set; }
    DockStyle Dock { get; set; }
    bool Enabled { get; set; }
    bool SoftDisabled { get; set; }
    event EventHandler Click;
    void SetExpoStyle(bool active);
    void Invalidate();
}

/// <summary>Host services exposed to the closed-source Memory OC module.</summary>
public interface IMemoryOcHost
{
    Form OwnerForm { get; }
    ToolTip Tips { get; }
    Control WorkspaceHost { get; }
    float UiScale { get; }
    Cpu? SharedCpu { get; }
    object IoLock { get; }
    bool IsExpertMode { get; }
    bool IsLaptop { get; }
    string LogsDir { get; }
    string ProfilesDir { get; }
    string DramDir { get; }
    string AppVersion { get; }

    Color Bg { get; }
    Color Surface { get; }
    Color SurfaceAlt { get; }
    Color TextHi { get; }
    Color TextLo { get; }
    Color Warn { get; }
    string FontFamily { get; }

    string Translate(string key);
    string Format(string key, params object[] args);
    void LogWrite(string message, string level = "INFO");
    void WriteDurableText(string path, string content);

    Panel CreateCard();
    Label CreateSectionTitle(string text);
    IMemoryOcButton CreatePrimaryButton(string text);
    IMemoryOcButton CreateGhostButton(string text);
    IMemoryOcButton CreateExpoChipButton(string text, Font font, int radius, Size size, Padding margin, bool enabled);
    void StyleUpDown(UpDownBase box, int width);
    void ApplyDarkCombo(ComboBox cb, bool center);
    void ApplyDarkSpin(NumericUpDown box);
    void ApplyDarkScrollBars(IntPtr handle);

    void ShowDialog(string message, string title);
    bool AskDialog(string message, string title, string okText, string cancelText, string? warning = null);
}

/// <summary>Live Memory OC page hosted inside MainForm.</summary>
public interface IMemoryOcPage
{
    Control Panel { get; }
    bool IsWriting { get; }
    void RefreshCommitButton();
}

/// <summary>Entry contract implemented by the protected closed-source Memory OC module.</summary>
public interface IMemoryOcEntry
{
    IMemoryOcPage Create(IMemoryOcHost host, ulong sessionToken);
}

/// <summary>Loads the optional closed-source Memory OC module from MemOcNative.dll into memory.</summary>
internal static class MemoryOcLoader
{
    private const string NativeDllName = "MemOcNative.dll";
    private const uint OpVaultOpen = 0x5A11A001u;
    private const uint OpVaultRelease = 0x5A11A002u;
    private const ulong ChallengeSalt = 0x9E3779B97F4A7C15UL;
    private const ulong ResponseSalt = 0xD1B54A32D192ED03UL;
    private const ulong TransportMaskSalt = 0x6A09E667F3BCC908UL;

    private static readonly object LoadGate = new();
    private static MemOcLoadContext? _context;
    private static IMemoryOcEntry? _entry;
    private static ulong _sessionToken;

    [DllImport(NativeDllName, EntryPoint = "_m1", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int NativeVault(uint op, ulong challenge, ref IntPtr ptr, ref int len, out ulong session);

    public static IMemoryOcPage Create(IMemoryOcHost host)
    {
        try
        {
            if (EnsureLoaded(out var entry, out ulong session) && entry != null)
                return entry.Create(host, session);
        }
        catch (Exception ex)
        {
            host.LogWrite($"Memory OC module failed to load: {ex.Message}", "WARN");
        }
        return new UnavailableMemoryOcPage(host);
    }

    private static bool EnsureLoaded(out IMemoryOcEntry? entry, out ulong session)
    {
        lock (LoadGate)
        {
            if (_entry != null && _sessionToken != 0)
            {
                entry = _entry;
                session = _sessionToken;
                return true;
            }

            string nativePath = Path.Combine(AppContext.BaseDirectory, NativeDllName);
            if (!File.Exists(nativePath))
            {
                entry = null;
                session = 0;
                return false;
            }

            Span<byte> nonceBytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(nonceBytes);
            ulong rawNonce = BinaryPrimitives.ReadUInt64LittleEndian(nonceBytes);
            if (rawNonce == 0) rawNonce = 1;
            ulong challenge = rawNonce ^ ChallengeSalt;

            IntPtr nativeBuf = IntPtr.Zero;
            int compressedLen = 0;
            int rc = NativeVault(OpVaultOpen, challenge, ref nativeBuf, ref compressedLen, out ulong maskedSession);
            if (rc != 0 || nativeBuf == IntPtr.Zero || compressedLen <= 0)
            {
                entry = null;
                session = 0;
                return false;
            }

            byte[] compressed = new byte[compressedLen];
            try
            {
                Marshal.Copy(nativeBuf, compressed, 0, compressedLen);
            }
            finally
            {
                NativeVault(OpVaultRelease, 0, ref nativeBuf, ref compressedLen, out _);
            }

            byte[]? assemblyBytes = null;
            try
            {
                UnmaskTransport(compressed, challenge ^ maskedSession ^ TransportMaskSalt);
                using var input = new MemoryStream(compressed, writable: false);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                assemblyBytes = output.ToArray();

                var alc = new MemOcLoadContext(AppContext.BaseDirectory);
                Assembly asm;
                using (var asmStream = new MemoryStream(assemblyBytes, writable: false))
                    asm = alc.LoadFromStream(asmStream);

                Type? entryType = asm.GetTypes()
                    .FirstOrDefault(t => !t.IsInterface && !t.IsAbstract && typeof(IMemoryOcEntry).IsAssignableFrom(t));
                if (entryType == null)
                {
                    entry = null;
                    session = 0;
                    return false;
                }

                ulong unmasked = maskedSession ^ SplitMix64(challenge ^ ResponseSalt);
                if (unmasked == 0)
                {
                    entry = null;
                    session = 0;
                    return false;
                }

                _context = alc;
                _entry = (IMemoryOcEntry)Activator.CreateInstance(entryType)!;
                _sessionToken = unmasked;
                entry = _entry;
                session = _sessionToken;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(compressed);
                if (assemblyBytes != null) CryptographicOperations.ZeroMemory(assemblyBytes);
            }
        }
    }

    private static void UnmaskTransport(byte[] data, ulong seed)
    {
        unchecked
        {
            ulong state = seed;
            int i = 0;
            while (i < data.Length)
            {
                ulong ks = SplitMix64(state);
                state += 0x9E3779B97F4A7C15UL;
                int chunk = Math.Min(8, data.Length - i);
                for (int b = 0; b < chunk; b++)
                    data[i + b] ^= (byte)(ks >> (b * 8));
                i += chunk;
            }
        }
    }

    private static ulong SplitMix64(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    private sealed class MemOcLoadContext : AssemblyLoadContext
    {
        private readonly string _baseDir;

        public MemOcLoadContext(string baseDir) : base(isCollectible: false) => _baseDir = baseDir;

        // Delegate managed references to Default so IMemoryOcHost, ZenStates.Core, and WinForms share exact type identity.
        // Also resolve host & ZenStates-Core by simple name so forks with different AssemblyVersion bind cleanly.
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, typeof(IMemoryOcHost).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return typeof(IMemoryOcHost).Assembly;
            if (string.Equals(assemblyName.Name, typeof(Cpu).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return typeof(Cpu).Assembly;
            return null;
        }

        // In-memory assemblies have no Location; resolve companion native DLLs from the application directory.
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string file = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : unmanagedDllName + ".dll";
            string fullPath = Path.Combine(_baseDir, file);
            return File.Exists(fullPath) ? LoadUnmanagedDllFromPath(fullPath) : IntPtr.Zero;
        }
    }

    private sealed class UnavailableMemoryOcPage : IMemoryOcPage
    {
        public Control Panel { get; }
        public bool IsWriting => false;
        public void RefreshCommitButton() { }

        public UnavailableMemoryOcPage(IMemoryOcHost host)
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = host.Bg,
                Margin = new Padding(0),
            };
            var card = host.CreateCard();
            card.Padding = new Padding(24, 20, 24, 20);
            var msg = new Label
            {
                Text = host.Translate("Memory OC module is not installed. This closed-source component is bundled only in official binary releases."),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = host.TextLo,
                BackColor = host.Surface,
                Font = new Font(host.FontFamily, 10F),
            };
            card.Controls.Add(msg);
            card.Controls.Add(host.CreateSectionTitle(host.Translate("Memory OC")));
            container.Controls.Add(card);
            Panel = container;
        }
    }
}

internal sealed partial class MainForm
{
    private Control _memoryPanel = null!;
    private IMemoryOcPage? _memOcPage;

    private Control BuildMemoryPanel()
    {
        _memOcPage = MemoryOcLoader.Create(new MemoryOcHostAdapter(this));
        return _memOcPage.Panel;
    }

    private sealed class PillButtonAdapter : IMemoryOcButton
    {
        private readonly PillButton _btn;

        public PillButtonAdapter(PillButton btn) => _btn = btn;

        public Control AsControl => _btn;
        public string Text { get => _btn.Text; set => _btn.Text = value; }
        public Font Font { get => _btn.Font; set => _btn.Font = value; }
        public Size Size { get => _btn.Size; set => _btn.Size = value; }
        public Padding Margin { get => _btn.Margin; set => _btn.Margin = value; }
        public DockStyle Dock { get => _btn.Dock; set => _btn.Dock = value; }
        public bool Enabled { get => _btn.Enabled; set => _btn.Enabled = value; }
        public bool SoftDisabled { get => _btn.SoftDisabled; set => _btn.SoftDisabled = value; }
        public event EventHandler Click { add => _btn.Click += value; remove => _btn.Click -= value; }
        public void Invalidate() => _btn.Invalidate();

        public void SetExpoStyle(bool active)
        {
            _btn.Normal = active ? Theme.AccentSoft : Theme.SurfaceAlt;
            _btn.Hover = active ? Theme.AccentSoftHover : Theme.SurfaceHover;
            _btn.Outline = active ? Theme.Accent : Theme.SpinBorder;
            _btn.ForeColor = active ? Theme.AccentText : Theme.TextHi;
            _btn.Invalidate();
        }
    }

    private sealed class MemoryOcHostAdapter : IMemoryOcHost
    {
        private readonly MainForm _owner;

        public MemoryOcHostAdapter(MainForm owner) => _owner = owner;

        public Form OwnerForm => _owner;
        public ToolTip Tips => _owner._tips;
        public Control WorkspaceHost => _owner._workspaceHost;
        public float UiScale => _owner._uiScale;
        public Cpu? SharedCpu
        {
            get
            {
                try { return RyzenSmu.SharedCpu; }
                catch { return null; }
            }
        }
        public object IoLock => RyzenSmu.IoLock;
        public bool IsExpertMode => AppSettings.Current.ExpertMode;
        public bool IsLaptop => SystemInfo.IsLaptop();
        public string LogsDir => Workspace.LogsDir;
        public string ProfilesDir => Workspace.ProfilesDir;
        public string DramDir => Workspace.DramDir;
        public string AppVersion => Application.ProductVersion.Split('+')[0];

        public Color Bg => Theme.Bg;
        public Color Surface => Theme.Surface;
        public Color SurfaceAlt => Theme.SurfaceAlt;
        public Color TextHi => Theme.TextHi;
        public Color TextLo => Theme.TextLo;
        public Color Warn => Theme.Warn;
        public string FontFamily => Theme.FontFamily;

        public string Translate(string key) => L.T(key);
        public string Format(string key, params object[] args) => L.F(key, args);
        public void LogWrite(string message, string level = "INFO") => Log.Write(message, level);
        public void WriteDurableText(string path, string content) => DurableIO.WriteAllText(path, content);

        public Panel CreateCard() => NewCard();
        public Label CreateSectionTitle(string text) => SectionTitle(text);
        public IMemoryOcButton CreatePrimaryButton(string text) => new PillButtonAdapter(Primary(text));
        public IMemoryOcButton CreateGhostButton(string text) => new PillButtonAdapter(Ghost(text));

        public IMemoryOcButton CreateExpoChipButton(string text, Font font, int radius, Size size, Padding margin, bool enabled)
        {
            var chip = new PillButton
            {
                Text = text,
                Font = font,
                Radius = radius,
                BackColor = Theme.Surface,
                Normal = Theme.SurfaceAlt,
                Hover = Theme.SurfaceHover,
                Disabled = Theme.SurfaceAlt,
                Outline = Theme.SpinBorder,
                ForeColor = Theme.TextHi,
                Size = size,
                Margin = margin,
                Enabled = enabled,
            };
            return new PillButtonAdapter(chip);
        }

        public void StyleUpDown(UpDownBase box, int width) => MainForm.StyleUpDown(box, width);
        public void ApplyDarkCombo(ComboBox cb, bool center) => DarkCombo.Apply(cb, center);
        public void ApplyDarkSpin(NumericUpDown box) => DarkSpin.Apply(box);
        public void ApplyDarkScrollBars(IntPtr handle) => DarkTitleBar.ApplyScrollBars(handle);

        public void ShowDialog(string message, string title) => ThemedDialog.Show(_owner, message, title);
        public bool AskDialog(string message, string title, string okText, string cancelText, string? warning = null) =>
            ThemedDialog.Ask(_owner, message, title, okText, cancelText, warning);
    }
}
