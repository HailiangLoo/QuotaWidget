using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace QuotaWidget.Core;

/// <summary>
/// Accepts an executable only if Windows validates its Authenticode signature and the signer
/// organisation matches. Do not trust a size/mtime cache for executable integrity.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    public const string AnthropicOrg = "Anthropic, PBC";

    public static bool IsSignedBy(string path, string organisation = AnthropicOrg)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); } catch { return false; }
        if (!fi.Exists) return false;
        return VerifyTrust(fi.FullName) && SignerOrganisation(fi.FullName) == organisation;
    }

    /// <summary>Hold a no-write/no-delete handle from verification through process creation.</summary>
    public static FileStream? OpenVerified(string path, string organisation = AnthropicOrg)
    {
        FileStream? handle=null;
        try
        {
            handle=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(IsSignedBy(path,organisation)) return handle;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        handle?.Dispose(); return null;
    }

    static string? SignerOrganisation(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return ParseOrganisation(cert.Subject);
        }
        catch
        {
            return null;
        }
    }

    static string? ParseOrganisation(string subject)
    {
        // Subject like: CN="Anthropic, PBC", O="Anthropic, PBC", L=San Francisco, ...
        var dn = new X500DistinguishedName(subject);
        foreach (var part in dn.EnumerateRelativeDistinguishedNames())
            if (part.GetSingleElementType().Value == "2.5.4.10") return part.GetSingleElementValue();
        return null;
    }

    static bool VerifyTrust(string path)
    {
        var file = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE: no network round-trip per poll
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x00001000,  // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
