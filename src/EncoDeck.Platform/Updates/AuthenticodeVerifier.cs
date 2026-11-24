// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace EncoDeck.Platform.Updates;

public enum SignatureState
{
    /// <summary>Signed, and Windows trusts the signature and its certificate chain.</summary>
    Valid,

    /// <summary>The file carries no Authenticode signature.</summary>
    Unsigned,

    /// <summary>Signed, but the signature, the file or the chain does not verify (or the file could not be read).</summary>
    Invalid,
}

/// <param name="SignerName">The signing certificate's subject common name (CN), when <see cref="State"/> is Valid.</param>
/// <param name="Status">WinVerifyTrust's result (an HRESULT) for the log.</param>
public sealed record SignatureInfo(SignatureState State, string? SignerName = null, int Status = 0);

/// <summary>Checks a file's code signature (a seam so the update policy can be tested without signed files).</summary>
public interface ISignatureVerifier
{
    SignatureInfo Verify(string path);
}

/// <summary>
/// Authenticode through WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2), the check Explorer and SmartScreen build on.
/// </summary>
/// <remarks>
/// Revocation is not checked (WTD_REVOKE_NONE) and URL retrieval is limited to the local cache
/// (WTD_CACHE_ONLY_URL_RETRIEVAL): the check runs when the user clicks "install", possibly offline, and must not
/// hang for CRL/OCSP/AIA download timeouts. Authenticode signatures normally carry their intermediate certificates,
/// so the chain still builds from the local stores; a timestamped signature stays valid after its certificate expires.
/// The installer's content is pinned by the published SHA-256 anyway; this check adds who signed it.
/// </remarks>
public sealed partial class AuthenticodeVerifier : ISignatureVerifier
{
    public static AuthenticodeVerifier Instance { get; } = new();

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    private const uint CertNameAttrType = 3;

    public unsafe SignatureInfo Verify(string path)
    {
        fixed (char* file = path)
        {
            var fileInfo = new WinTrustFileInfo { Size = (uint)sizeof(WinTrustFileInfo), FilePath = (nint)file };
            var data = new WinTrustData
            {
                Size = (uint)sizeof(WinTrustData),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                File = (nint)(&fileInfo),
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdCacheOnlyUrlRetrieval,
            };
            Guid action = GenericVerifyV2;
            // INVALID_HANDLE_VALUE: no interactive user, never any UI.
            int result = WinVerifyTrust(-1, &action, &data);
            try
            {
                if (result == 0) return new SignatureInfo(SignatureState.Valid, SignerName(data.StateData), 0);
                bool unsigned = result is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown;
                return new SignatureInfo(unsigned ? SignatureState.Unsigned : SignatureState.Invalid, null, result);
            }
            finally
            {
                // The state data is held until closed, whatever the result.
                data.StateAction = WtdStateActionClose;
                WinVerifyTrust(-1, &action, &data);
            }
        }
    }

    /// <summary>The leaf certificate of the first signer, from the verified state (the certificate checked, not a re-read).</summary>
    private static unsafe string? SignerName(nint stateData)
    {
        nint provider = WTHelperProvDataFromStateData(stateData);
        if (provider == 0) return null;
        nint signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if (signer == 0) return null;
        nint cert = WTHelperGetProvCertFromChain(signer, 0);
        if (cert == 0) return null;
        // CRYPT_PROVIDER_CERT: DWORD cbStruct, then PCCERT_CONTEXT pCert at pointer alignment.
        nint context = Marshal.ReadIntPtr(cert, IntPtr.Size);
        if (context == 0) return null;
        ReadOnlySpan<byte> commonNameOid = "2.5.4.3\0"u8;
        fixed (byte* oid = commonNameOid)
        {
            uint length = CertGetNameStringW(context, CertNameAttrType, 0, (nint)oid, null, 0);
            if (length <= 1) return null;
            char[] buffer = new char[length];
            fixed (char* text = buffer)
            {
                length = CertGetNameStringW(context, CertNameAttrType, 0, (nint)oid, text, length);
                return length <= 1 ? null : new string(text, 0, (int)length - 1);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    /// <summary>WINTRUST_DATA (the union is a single pointer).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(nint window, Guid* action, WinTrustData* data);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex, int counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvCertFromChain(nint signer, uint certIndex);

    [LibraryImport("crypt32.dll")]
    private static unsafe partial uint CertGetNameStringW(nint certContext, uint type, uint flags, nint typeParameter, char* name, uint length);
}
