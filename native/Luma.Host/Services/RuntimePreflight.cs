using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Formats.Asn1;
using Microsoft.Win32;

namespace Luma.Host.Services;

public interface IRegistryValueReader
{
    string? ReadString(RegistryHive hive, RegistryView view, string subKey, string valueName);
}

public sealed class WindowsRegistryValueReader : IRegistryValueReader
{
    public string? ReadString(RegistryHive hive, RegistryView view, string subKey, string valueName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subKey);
        return key?.GetValue(valueName) as string;
    }
}

public static class RuntimePreflight
{
    public const string ClientRegistryPath = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    public const string BootstrapperFileName = "MicrosoftEdgeWebview2Setup.exe";
    public const int InstallTimedOut = -2;
    public static readonly Version MinimumWindowsVersion = new(10, 0, 19045);

    public static bool IsSupportedWindows(Version version) => version >= MinimumWindowsVersion;

    public static bool IsSupportedArchitecture(Architecture architecture) => architecture == Architecture.X64;

    public static bool IsUsableRuntimeVersion(string? value) =>
        Version.TryParse(value, out var version) && version > new Version(0, 0, 0, 0);

    public static bool IsWebView2Installed(IRegistryValueReader registry, bool is64BitOperatingSystem)
    {
        if (IsUsableRuntimeVersion(registry.ReadString(RegistryHive.CurrentUser, RegistryView.Default, ClientRegistryPath, "pv")))
            return true;

        var machineView = is64BitOperatingSystem ? RegistryView.Registry32 : RegistryView.Default;
        return IsUsableRuntimeVersion(registry.ReadString(RegistryHive.LocalMachine, machineView, ClientRegistryPath, "pv"));
    }

    public static RuntimeInstallResult InstallWithRetry(Func<int> launchInstaller, Func<bool> isRuntimeInstalled, int attempts = 2)
    {
        var exitCodes = new List<int>();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var exitCode = launchInstaller();
            exitCodes.Add(exitCode);
            if (isRuntimeInstalled()) return new(true, exitCodes);
            if (exitCode == InstallTimedOut) break;
        }
        return new(false, exitCodes);
    }

    public static bool HasTrustedMicrosoftSignature(string path)
    {
        if (!File.Exists(path) || NativeMethods.VerifyEmbeddedSignature(path) != 0) return false;
        try
        {
#pragma warning disable SYSLIB0057 // Authenticode has no X509CertificateLoader equivalent in .NET 10.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return HasMicrosoftPublisher(certificate.SubjectName);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    internal static bool HasMicrosoftPublisher(X500DistinguishedName subject)
    {
        try
        {
            var reader = new AsnReader(subject.RawData, AsnEncodingRules.DER);
            var name = reader.ReadSequence();
            while (name.HasData)
            {
                var relativeName = name.ReadSetOf(skipSortOrderValidation: true);
                while (relativeName.HasData)
                {
                    var attribute = relativeName.ReadSequence();
                    var oid = attribute.ReadObjectIdentifier();
                    if (oid == "2.5.4.10")
                    {
                        var tag = attribute.PeekTag();
                        string? organization = tag.TagClass == TagClass.Universal ? (UniversalTagNumber)tag.TagValue switch
                        {
                            UniversalTagNumber.UTF8String => attribute.ReadCharacterString(UniversalTagNumber.UTF8String),
                            UniversalTagNumber.PrintableString => attribute.ReadCharacterString(UniversalTagNumber.PrintableString),
                            UniversalTagNumber.IA5String => attribute.ReadCharacterString(UniversalTagNumber.IA5String),
                            UniversalTagNumber.BMPString => attribute.ReadCharacterString(UniversalTagNumber.BMPString),
                            UniversalTagNumber.T61String => attribute.ReadCharacterString(UniversalTagNumber.T61String),
                            _ => null
                        } : null;
                        if (string.Equals(organization, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    while (attribute.HasData) attribute.ReadEncodedValue();
                }
            }
            return false;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            public string FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public string? UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, [In] ref Guid actionId, [In] ref WinTrustData trustData);

        public static int VerifyEmbeddedSignature(string path)
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path,
                FileHandle = IntPtr.Zero, KnownSubject = IntPtr.Zero
            };
            var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, filePointer, false);
                var trustData = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 0,
                    UnionChoice = 1, FileInfo = filePointer, StateAction = 0,
                    ProviderFlags = 0x00000010, UiContext = 0
                };
                var action = WinTrustActionGenericVerifyV2;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
                Marshal.FreeHGlobal(filePointer);
            }
        }
    }
}

public sealed record RuntimeInstallResult(bool Succeeded, IReadOnlyList<int> ExitCodes);
