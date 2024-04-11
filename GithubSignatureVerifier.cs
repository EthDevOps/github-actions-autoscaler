using System.Security.Cryptography;
using System.Text;

namespace GithubActionsOrchestrator;

public static class GithubSignatureVerifier
{
    public static void VerifySignature(string payloadBody, string secretToken, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            throw new Exception("x-hub-signature-256 header is missing!");
        }

        HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretToken));
        byte[] hashPayload = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBody));

        string expectedSignature = "sha256=" + BitConverter.ToString(hashPayload).Replace("-", "").ToLower();

        if (expectedSignature != signatureHeader)
        {
            throw new Exception("Request signatures didn't match!");
        }
    }
}