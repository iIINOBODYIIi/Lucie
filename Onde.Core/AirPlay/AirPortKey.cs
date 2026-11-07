using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Onde.Core.AirPlay;

/// <summary>
/// Clé RSA des bornes AirPort Express (publique depuis 2011, utilisée par tous les récepteurs
/// AirPlay open source comme shairport-sync). Sert à déchiffrer la clé AES du flux et à
/// répondre au défi "Apple-Challenge".
/// </summary>
internal static class AirPortKey
{
    private const string Pem = @"
-----BEGIN RSA PRIVATE KEY-----
MIIEpQIBAAKCAQEA59dE8qLieItsH1WgjrcFRKj6eUWqi+bGLOX1HL3U3GhC/j0Q
g90u3sG/1CUtwC5vOYvfDmFI6oSFXi5ELabWJmT2dKHzBJKa3k9ok+8t9ucRqMd6
DZHJ2YCCLlDRKSKv6kDqnw4UwPdpOMXziC/AMj3Z/lUVX1G7WSHCAWKf1zNS1eLv
qr+boEjXuBOitnZ/bDzPHrTOZz0Dew0uowxf/+sG+NCK3eQJVxqcaJ/vEHKIVd2M
+5qL71yJQ+87X6oV3eaYvt3zWZYD6z5vYTcrtij2VZ9Zmni/UAaHqn9JdsBWLUEp
VviYnhimNVvYFZeCXg/IdTQ+x4IRdiXNv5hEewIDAQABAoIBAQDl8Axy9XfWBLmk
zkEiqoSwF0PsmVrPzH9KsnwLGH+QZlvjWd8SWYGN7u1507HvhF5N3drJoVU3O14n
DY4TFQAaLlJ9VM35AApXaLyY1ERrN7u9ALKd2LUwYhM7Km539O4yUFYikE2nIPsc
EsA5ltpxOgUGCY7b7ez5NtD6nL1ZKauw7aNXmVAvmJTcuPxWmoktF3gDJKK2wxZu
NGcJE0uFQEG4Z3BrWP7yoNuSK3dii2jmlpPHr0O/KnPQtzI3eguhe0TwUem/eYSd
yzMyVx/YpwkzwtYL3sR5k0o9rKQLtvLzfAqdBxBurcizaaA/L0HIgAmOit1GJA2s
aMxTVPNhAoGBAPfgv1oeZxgxmotiCcMXFEQEWflzhWYTsXrhUIuz5jFua39GLS99
ZEErhLdrwj8rDDViRVJ5skOp9zFvlYAHs0xh92ji1E7V/ysnKBfsMrPkk5KSKPrn
jndMoPdevWnVkgJ5jxFuNgxkOLMuG9i53B4yMvDTCRiIPMQ++N2iLDaRAoGBAO9v
//mU8eVkQaoANf0ZoMjW8CN4xwWA2cSEIHkd9AfFkftuv8oyLDCG3ZAf0vrhrrtk
rfa7ef+AUb69DNggq4mHQAYBp7L+k5DKzJrKuO0r+R0YbY9pZD1+/g9dVt91d6LQ
NepUE/yY2PP5CNoFmjedpLHMOPFdVgqDzDFxU8hLAoGBANDrr7xAJbqBjHVwIzQ4
To9pb4BNeqDndk5Qe7fT3+/H1njGaC0/rXE0Qb7q5ySgnsCb3DvAcJyRM9SJ7OKl
Gt0FMSdJD5KG0XPIpAVNwgpXXH5MDJg09KHeh0kXo+QA6viFBi21y340NonnEfdf
54PX4ZGS/Xac1UK+pLkBB+zRAoGAf0AY3H3qKS2lMEI4bzEFoHeK3G895pDaK3TF
BVmD7fV0Zhov17fegFPMwOII8MisYm9ZfT2Z0s5Ro3s5rkt+nvLAdfC/PYPKzTLa
lpGSwomSNYJcB9HNMlmhkGzc1JnLYT4iyUyx6pcZBmCd8bD0iwY/FzcgNDaUmbX9
+XDvRA0CgYEAkE7pIPlE71qvfJQgoA9em0gILAuE4Pu13aKiJnfft7hIjbK+5kyb
3TysZvoyDnb3HOKvInK7vXbKuU4ISgxB2bB3HcYzQMGsz1qJ2gG0N5hvJpzwwhbh
XqFKA4zaaSrw622wDniAK5MlIE0tIAKKP4yxNGjoD2QYjhBGuhvkWKY=
-----END RSA PRIVATE KEY-----
";

    private static readonly Lazy<RSA> Rsa = new(() =>
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(Pem.Trim());
        return rsa;
    });

    /// <summary>Déchiffre la clé AES (RSA-OAEP / SHA-1).</summary>
    public static byte[] DecryptAesKey(byte[] encrypted) => Rsa.Value.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1);

    /// <summary>Signature RSA "brute" PKCS#1 v1.5 (type 1) sans hachage, exigée par Apple-Challenge.</summary>
    public static byte[] SignRaw(byte[] data)
    {
        var p = Rsa.Value.ExportParameters(true);
        int k = p.Modulus!.Length;
        var em = new byte[k];
        em[0] = 0x00; em[1] = 0x01;
        int ps = k - 3 - data.Length;
        for (int i = 0; i < ps; i++) em[2 + i] = 0xFF;
        em[2 + ps] = 0x00;
        Buffer.BlockCopy(data, 0, em, 3 + ps, data.Length);

        var m = new BigInteger(em, isUnsigned: true, isBigEndian: true);
        var d = new BigInteger(p.D, isUnsigned: true, isBigEndian: true);
        var n = new BigInteger(p.Modulus, isUnsigned: true, isBigEndian: true);
        var s = BigInteger.ModPow(m, d, n).ToByteArray(isUnsigned: true, isBigEndian: true);
        if (s.Length == k) return s;
        var padded = new byte[k];
        Buffer.BlockCopy(s, 0, padded, k - s.Length, s.Length);
        return padded;
    }

    /// <summary>Pour les tests : chiffrement avec la clé publique.</summary>
    internal static byte[] EncryptForTest(byte[] data) => Rsa.Value.Encrypt(data, RSAEncryptionPadding.OaepSHA1);

    internal static byte[] PublicOp(byte[] sig)
    {
        var p = Rsa.Value.ExportParameters(false);
        var s = new BigInteger(sig, isUnsigned: true, isBigEndian: true);
        var e = new BigInteger(p.Exponent!, isUnsigned: true, isBigEndian: true);
        var n = new BigInteger(p.Modulus!, isUnsigned: true, isBigEndian: true);
        return BigInteger.ModPow(s, e, n).ToByteArray(isUnsigned: true, isBigEndian: true);
    }
}
