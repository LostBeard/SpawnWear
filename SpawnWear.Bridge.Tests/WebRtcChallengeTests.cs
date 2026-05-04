using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.Cryptography.DotNet;
using SpawnWear.Bridge.WebRtc;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Wire-format + crypto round-trip tests for the WebRTC data-channel
/// mutual verification challenge. Pure byte packing locked here; the
/// crypto round-trip uses real Ed25519 to prove a peer that signs the
/// challenge with one keypair can be verified by another peer holding
/// the public key.
/// </summary>
public class WebRtcChallengeTests
{
    [Fact]
    public void GenerateNonce_returns_32_random_bytes()
    {
        var a = WebRtcChallenge.GenerateNonce();
        var b = WebRtcChallenge.GenerateNonce();
        Assert.Equal(32, a.Length);
        Assert.Equal(32, b.Length);
        Assert.NotEqual(a, b); // exceedingly unlikely to collide
    }

    [Fact]
    public void PackRequest_round_trips_nonce_bytes_with_defensive_copy()
    {
        var nonce = WebRtcChallenge.GenerateNonce();
        var req = WebRtcChallenge.PackRequest(nonce);
        Assert.Equal(nonce, req);

        // Mutating the source should NOT affect the packed output
        // (defensive copy expected).
        nonce[0] = (byte)~nonce[0];
        Assert.NotEqual(nonce, req);
    }

    [Fact]
    public void PackResponse_lays_out_nonce_then_signature()
    {
        var nonce = new byte[32];
        var sig   = new byte[64];
        for (int i = 0; i < 32; i++) nonce[i] = (byte)(0x10 + i);
        for (int i = 0; i < 64; i++) sig[i]   = (byte)(0x80 + i);

        var resp = WebRtcChallenge.PackResponse(nonce, sig);

        Assert.Equal(96, resp.Length);
        for (int i = 0; i < 32; i++) Assert.Equal(nonce[i], resp[i]);
        for (int i = 0; i < 64; i++) Assert.Equal(sig[i],   resp[32 + i]);
    }

    [Fact]
    public void ParseResponse_round_trips_PackResponse()
    {
        var nonce = new byte[32];
        var sig   = new byte[64];
        for (int i = 0; i < 32; i++) nonce[i] = (byte)(0x10 + i);
        for (int i = 0; i < 64; i++) sig[i]   = (byte)(0x80 + i);

        var resp = WebRtcChallenge.PackResponse(nonce, sig);
        var (gotN, gotS) = WebRtcChallenge.ParseResponse(resp);

        Assert.Equal(nonce, gotN);
        Assert.Equal(sig,   gotS);
    }

    [Fact]
    public void ParseResponse_rejects_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => WebRtcChallenge.ParseResponse(new byte[95]));
        Assert.Throws<ArgumentException>(() => WebRtcChallenge.ParseResponse(new byte[97]));
    }

    [Fact]
    public async Task End_to_end_signed_response_verifies_under_signers_pubkey()
    {
        var crypto = new DotNetCrypto();

        // The responder has an Ed25519 keypair (same as the watch will
        // have in production). They receive a challenge from the requester.
        using var responderKey = await crypto.GenerateEd25519Key();
        var responderPubSpki = await crypto.ExportPublicKeySpki(responderKey);

        // Requester sends a fresh nonce.
        var nonce = WebRtcChallenge.GenerateNonce();

        // Responder signs the challenge domain.
        var signedDomain = WebRtcChallenge.SignedDomain(nonce);
        var signature = await crypto.Sign(responderKey, signedDomain);
        Assert.Equal(WebRtcChallenge.SignatureLength, signature.Length);

        // Responder packs the response and sends it back.
        var responsePayload = WebRtcChallenge.PackResponse(nonce, signature);

        // Requester parses the response, confirms the echoed nonce
        // matches what they sent, and verifies the signature against
        // the stored responder pubkey.
        var (echoedNonce, echoedSig) = WebRtcChallenge.ParseResponse(responsePayload);
        Assert.Equal(nonce, echoedNonce);

        using var verifyKey = await crypto.ImportEd25519Key(responderPubSpki);
        var ok = await crypto.Verify(verifyKey, WebRtcChallenge.SignedDomain(echoedNonce), echoedSig);
        Assert.True(ok);
    }

    [Fact]
    public async Task End_to_end_wrong_pubkey_fails_verification()
    {
        var crypto = new DotNetCrypto();
        using var realResponder = await crypto.GenerateEd25519Key();
        using var imposter       = await crypto.GenerateEd25519Key();

        var nonce = WebRtcChallenge.GenerateNonce();
        var signature = await crypto.Sign(realResponder, WebRtcChallenge.SignedDomain(nonce));
        var response = WebRtcChallenge.PackResponse(nonce, signature);
        var (n, s) = WebRtcChallenge.ParseResponse(response);

        // Verify with the imposter's pubkey - should FAIL.
        var imposterSpki = await crypto.ExportPublicKeySpki(imposter);
        using var imposterVerify = await crypto.ImportEd25519Key(imposterSpki);
        var ok = await crypto.Verify(imposterVerify, WebRtcChallenge.SignedDomain(n), s);
        Assert.False(ok);
    }

    [Fact]
    public void Layout_constants_match_concatenated_field_lengths()
    {
        Assert.Equal(32, WebRtcChallenge.NonceLength);
        Assert.Equal(64, WebRtcChallenge.SignatureLength);
        Assert.Equal(32, WebRtcChallenge.ChallengeRequestLength);
        Assert.Equal(96, WebRtcChallenge.ChallengeResponseLength);
    }
}
