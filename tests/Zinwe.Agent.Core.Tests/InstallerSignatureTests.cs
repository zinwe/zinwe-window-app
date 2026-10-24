using Zinwe.Agent.Core.Update;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// FIX-016: an update installs only when it carries an intact Authenticode signature by a pinned Zinwe certificate.
/// The live tests use files every Windows test box has (the .NET runtime is Microsoft-signed), plus the installed
/// Zinwe agent when there is one (signed with the self-signed dev certificate, so its chain is usually NOT trusted).
/// </summary>
public sealed class InstallerSignatureTests : IDisposable
{
    private const string DevCert = "6FFAC69585F869B2FEF41581A8AA78DF7BD418E2";
    private static readonly string[] Pinned = [DevCert];
    private static readonly string InstalledService = @"C:\Program Files\Zinwe Agent\service\Zinwe.Agent.Service.exe";

    private readonly TempDir _dir = new();

    public InstallerSignatureTests() => Directory.CreateDirectory(_dir.Path);

    // ------------------------------------------------------------- the decision

    [Theory]
    [InlineData(AuthenticodeSignatureCheck.Ok)]
    [InlineData(AuthenticodeSignatureCheck.CertEUntrustedRoot)] // self-signed dev cert on a PC that never imported it
    [InlineData(AuthenticodeSignatureCheck.CertEChaining)]
    public void An_intact_signature_by_a_pinned_certificate_is_accepted(int result)
    {
        var (ok, reason) = AuthenticodeSignatureCheck.Evaluate(new(result, DevCert, "CN=Zinwe Dev (SKYTECHNO)"), Pinned);
        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData(AuthenticodeSignatureCheck.TrustENoSignature, "no digital signature")]
    [InlineData(AuthenticodeSignatureCheck.TrustEBadDigest, "changed after it was signed")]
    [InlineData(AuthenticodeSignatureCheck.TrustESubjectNotTrusted, "isn't valid")]
    [InlineData(AuthenticodeSignatureCheck.TrustEExplicitDistrust, "marked untrusted")]
    [InlineData(AuthenticodeSignatureCheck.CertERevoked, "revoked")]
    [InlineData(AuthenticodeSignatureCheck.CertEExpired, "expired")]
    public void A_broken_signature_is_refused_even_with_a_pinned_signer(int result, string why)
    {
        var (ok, reason) = AuthenticodeSignatureCheck.Evaluate(new(result, DevCert, "CN=Zinwe Dev (SKYTECHNO)"), Pinned);
        Assert.False(ok);
        Assert.Contains(why, reason);
    }

    [Fact]
    public void A_valid_signature_by_anyone_else_is_refused()
    {
        // e.g. a self-signed "CN=Zinwe Dev (SKYTECHNO)" made by someone else: same name, different certificate.
        var (ok, reason) = AuthenticodeSignatureCheck.Evaluate(
            new(AuthenticodeSignatureCheck.CertEUntrustedRoot, "1111111111111111111111111111111111111111", "CN=Zinwe Dev (SKYTECHNO)"), Pinned);
        Assert.False(ok);
        Assert.Contains("isn't a Zinwe release certificate", reason);
        Assert.False(AuthenticodeSignatureCheck.Evaluate(new(AuthenticodeSignatureCheck.Ok, null, null), Pinned).Ok);
    }

    [Fact]
    public void Thumbprints_compare_without_spaces_or_case()
    {
        var spaced = "6f fa c6 95 85 f8 69 b2 fe f4 15 81 a8 aa 78 df 7b d4 18 e2";
        Assert.True(AuthenticodeSignatureCheck.Evaluate(new(AuthenticodeSignatureCheck.Ok, spaced, "CN=x"), Pinned).Ok);
    }

    [Fact]
    public void The_dev_certificate_is_pinned() => Assert.Contains(DevCert, ZinweSigners.Thumbprints);

    [Fact]
    public void The_backup_signing_certificate_is_pinned_too() =>
        Assert.Contains("9A8A1A5E85B26CE9C55D4CA0ADAC5AA4F3D164CB", ZinweSigners.Thumbprints);

    // ------------------------------------------------------------- real files through WinVerifyTrust

    private static string MicrosoftSignedFile => typeof(object).Assembly.Location; // System.Private.CoreLib.dll

    [Fact]
    public void A_microsoft_signed_file_verifies_but_is_not_a_zinwe_release()
    {
        var inspection = AuthenticodeSignatureCheck.Inspect(MicrosoftSignedFile);

        Assert.Equal(AuthenticodeSignatureCheck.Ok, inspection.TrustResult);
        Assert.Matches("^[0-9A-F]{40}$", inspection.SignerThumbprint);
        Assert.Contains("Microsoft", inspection.SignerSubject);
        var e = Assert.Throws<UpdateException>(() => new AuthenticodeSignatureCheck().Verify(MicrosoftSignedFile));
        Assert.Contains("isn't signed by Zinwe", e.Message);
        Assert.Contains("isn't a Zinwe release certificate", e.Detail);
        // Had it been on the pin list, it would pass: the read thumbprint is really the signer's.
        new AuthenticodeSignatureCheck([inspection.SignerThumbprint!]).Verify(MicrosoftSignedFile);
    }

    [Fact]
    public void A_changed_byte_breaks_the_signature_even_for_a_pinned_signer()
    {
        var thumbprint = AuthenticodeSignatureCheck.Inspect(MicrosoftSignedFile).SignerThumbprint!;
        var copy = Tampered(MicrosoftSignedFile);

        Assert.Equal(AuthenticodeSignatureCheck.TrustEBadDigest, AuthenticodeSignatureCheck.Inspect(copy).TrustResult);
        var e = Assert.Throws<UpdateException>(() => new AuthenticodeSignatureCheck([thumbprint]).Verify(copy));
        Assert.Contains("changed after it was signed", e.Detail);
    }

    [Fact]
    public void An_unsigned_file_is_refused()
    {
        var file = Path.Combine(_dir.Path, "ZinweAgent-9.9.9.msi");
        File.WriteAllBytes(file, [.. Enumerable.Range(0, 4096).Select(i => (byte)i)]);

        Assert.NotEqual(AuthenticodeSignatureCheck.Ok, AuthenticodeSignatureCheck.Inspect(file).TrustResult);
        Assert.Throws<UpdateException>(() => new AuthenticodeSignatureCheck(Pinned).Verify(file));
    }

    [Fact]
    public void The_installed_agent_signed_with_the_dev_certificate_passes_and_a_changed_copy_does_not()
    {
        if (!File.Exists(InstalledService))
        {
            return; // no Zinwe agent on this test box
        }
        var inspection = AuthenticodeSignatureCheck.Inspect(InstalledService);
        if (inspection.SignerThumbprint != DevCert)
        {
            return; // an unsigned or differently signed local build is installed
        }
        // Ok where the dev .cer was imported as a trusted root, CERT_E_UNTRUSTEDROOT everywhere else: both pass.
        Assert.Contains(inspection.TrustResult, new[] { AuthenticodeSignatureCheck.Ok, AuthenticodeSignatureCheck.CertEUntrustedRoot });
        new AuthenticodeSignatureCheck().Verify(InstalledService);

        var copy = Tampered(InstalledService);
        Assert.Equal(AuthenticodeSignatureCheck.TrustEBadDigest, AuthenticodeSignatureCheck.Inspect(copy).TrustResult);
        Assert.Throws<UpdateException>(() => new AuthenticodeSignatureCheck().Verify(copy));
    }

    /// <summary>A copy with one byte flipped in the middle (code/data, which the signature covers).</summary>
    private string Tampered(string source)
    {
        var bytes = File.ReadAllBytes(source);
        bytes[bytes.Length / 2] ^= 0xFF;
        var copy = Path.Combine(_dir.Path, "tampered-" + Path.GetFileName(source));
        File.WriteAllBytes(copy, bytes);
        return copy;
    }

    public void Dispose() => _dir.Dispose();
}
