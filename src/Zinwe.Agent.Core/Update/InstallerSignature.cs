using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Zinwe.Agent.Core.Update;

/// <summary>Proves a downloaded installer was signed by Zinwe; throws <see cref="UpdateException"/> when it wasn't.</summary>
public interface IInstallerSignatureCheck
{
    void Verify(string path);
}

/// <summary>
/// The certificates Zinwe signs releases with (SHA-1 thumbprints, as Windows and build.ps1 -CertThumbprint show them).
/// An update is installed only if its Authenticode signer is one of these. Adding a certificate (the OV certificate,
/// a renewal) is a code change shipped in a release signed with a certificate that is ALREADY on this list, so PCs
/// learn the new one before the first release signed only with it.
/// </summary>
public static class ZinweSigners
{
    public static IReadOnlyList<string> Thumbprints { get; } =
    [
        // "CN=Zinwe Dev (SKYTECHNO), O=SKYTECHNO, C=IN" - self-signed, valid 2026-10-17 .. 2028-10-17 (13-release-process.md).
        "6FFAC69585F869B2FEF41581A8AA78DF7BD418E2",
        // The OV/IV code-signing certificate goes here once bought (see 13-release-process.md, "Signing").
    ];
}

/// <summary>What WinVerifyTrust said about a file, and who signed it.</summary>
public sealed record SignatureInspection(int TrustResult, string? SignerThumbprint, string? SignerSubject);

/// <summary>
/// FIX-016: Authenticode check of the downloaded MSI, on top of the SHA-256 check.
///
/// <para><b>The logic.</b> WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2, no UI, no revocation check, chain
/// building from the local cache only) runs the Authenticode provider steps in order: load the embedded signature,
/// hash the file and compare it with the signed digest, verify the signer's signature over that digest, then build
/// and judge the certificate chain. A step only runs when the ones before it passed, and its error is what comes back:
/// a changed file gives TRUST_E_BAD_DIGEST, no signature TRUST_E_NOSIGNATURE, a broken signature
/// TRUST_E_SUBJECT_NOT_TRUSTED / CRYPT_E_*; only the last step can give CERT_E_UNTRUSTEDROOT / CERT_E_CHAINING.</para>
/// <para>So we accept exactly three results - S_OK (signed, chain trusted), CERT_E_UNTRUSTEDROOT (signed, intact,
/// but the root isn't trusted on this PC: our self-signed certificate on a PC that never imported it), and
/// CERT_E_CHAINING (signed, intact, chain couldn't be completed offline) - and in every one of them the signature's
/// integrity was verified. Then the signer certificate (from the same WinVerifyTrust state, so it is the certificate
/// that was verified, not a second parse) must have a thumbprint on <see cref="ZinweSigners"/>. Pinning the
/// certificate is what makes skipping the chain safe: anyone can make a self-signed certificate called "Zinwe", no
/// one else can sign with ours. Everything else is refused - including TRUST_E_EXPLICIT_DISTRUST (an admin put the
/// certificate in Untrusted Certificates), CERT_E_REVOKED, CERT_E_EXPIRED (expired and not timestamped) and
/// CERT_E_WRONG_USAGE.</para>
/// </summary>
public sealed class AuthenticodeSignatureCheck(IReadOnlyCollection<string>? pinnedThumbprints = null) : IInstallerSignatureCheck
{
    public const int Ok = 0;
    public const int TrustENoSignature = unchecked((int)0x800B0100);
    public const int TrustEBadDigest = unchecked((int)0x80096010);
    public const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);
    public const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    public const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    public const int CertEChaining = unchecked((int)0x800B010A);
    public const int CertERevoked = unchecked((int)0x800B010C);
    public const int CertEExpired = unchecked((int)0x800B0101);

    private const string NotOurs = "This update isn't signed by Zinwe, so it wasn't installed. Please tell your admin.";

    private readonly IReadOnlyCollection<string> _pinned = pinnedThumbprints ?? ZinweSigners.Thumbprints;

    public void Verify(string path)
    {
        var inspection = Inspect(path);
        var (ok, reason) = Evaluate(inspection, _pinned);
        if (!ok)
        {
            throw new UpdateException(NotOurs, reason);
        }
    }

    /// <summary>The decision, separate from the Windows call so every branch is unit-tested.</summary>
    public static (bool Ok, string Reason) Evaluate(SignatureInspection inspection, IReadOnlyCollection<string> pinned)
    {
        var code = $"0x{inspection.TrustResult:X8}";
        if (inspection.TrustResult is not (Ok or CertEUntrustedRoot or CertEChaining))
        {
            var what = inspection.TrustResult switch
            {
                TrustENoSignature => "the installer has no digital signature",
                TrustEBadDigest => "the installer was changed after it was signed",
                TrustEExplicitDistrust => "the signing certificate is marked untrusted on this PC",
                CertERevoked => "the signing certificate was revoked",
                CertEExpired => "the signing certificate expired and the signature has no timestamp",
                _ => "the digital signature isn't valid",
            };
            return (false, $"Signature check failed ({code}): {what}.");
        }
        var thumbprint = Normalize(inspection.SignerThumbprint);
        if (thumbprint.Length == 0 || !pinned.Any(p => Normalize(p) == thumbprint))
        {
            return (false, $"Signature check failed: signed by '{inspection.SignerSubject}' ({thumbprint}), which isn't a Zinwe release certificate.");
        }
        var chain = inspection.TrustResult == Ok ? "chain trusted" : $"chain not trusted on this PC ({code}), certificate pinned";
        return (true, $"Signed by '{inspection.SignerSubject}' ({thumbprint}); {chain}.");
    }

    private static string Normalize(string? thumbprint) =>
        new string((thumbprint ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>Runs WinVerifyTrust on <paramref name="path"/> and reads the signer certificate it verified.</summary>
    public static SignatureInspection Inspect(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(Path.GetFullPath(path));
        var fileInfo = new WintrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = pathPtr,
        };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, filePtr, false);
        var data = new WintrustData
        {
            Size = (uint)Marshal.SizeOf<WintrustData>(),
            UiChoice = WtdUiNone,
            RevocationChecks = WtdRevokeNone,
            UnionChoice = WtdChoiceFile,
            File = filePtr,
            StateAction = WtdStateActionVerify,
            ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
        };
        var action = GenericVerifyV2;
        try
        {
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data); // INVALID_HANDLE_VALUE: no UI at all
            var (thumbprint, subject) = Signer(data.StateData);
            return new SignatureInspection(result, thumbprint, subject);
        }
        finally
        {
            data.StateAction = WtdStateActionClose;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static (string? Thumbprint, string? Subject) Signer(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero)
        {
            return (null, null);
        }
        var provData = WTHelperProvDataFromStateData(stateData);
        if (provData == IntPtr.Zero)
        {
            return (null, null);
        }
        var signer = WTHelperGetProvSignerFromChain(provData, 0, false, 0);
        if (signer == IntPtr.Zero)
        {
            return (null, null);
        }
        var providerCert = WTHelperGetProvCertFromChain(signer, 0); // index 0 = the signing (leaf) certificate
        if (providerCert == IntPtr.Zero)
        {
            return (null, null);
        }
        // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... }: pCert sits after the pointer-aligned DWORD.
        var certContext = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        if (certContext == IntPtr.Zero)
        {
            return (null, null);
        }
        using var certificate = new X509Certificate2(certContext); // duplicates the context; ours to dispose
        return (certificate.Thumbprint, certificate.Subject);
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    // WINTRUST_ACTION_GENERIC_VERIFY_V2 (the Authenticode policy provider).
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    // Classic DllImport (not LibraryImport) so no unsafe/csproj change is needed in the shared Core project.
#pragma warning disable SYSLIB1054
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WintrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr provData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);
#pragma warning restore SYSLIB1054
}
