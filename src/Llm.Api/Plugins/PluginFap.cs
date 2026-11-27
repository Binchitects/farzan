// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Api.Plugins;

/// <summary>What checking a package's signature found: signed or not, by which key, and whether it holds.</summary>
/// <param name="Trusted">Signed by one of the keys given as trusted (and checked with that key, not the one the package carries).</param>
public sealed record PluginSignature(bool IsSigned, string? Fingerprint, bool Trusted = false);

/// <summary>
/// A plugin package (.fap) as the plugin framework makes it: a zip of plugin.yaml and its files, META-INF/files.json with
/// each file's SHA-256 and size, and META-INF/signature (ECDSA P-256 over files.json's exact bytes, with the key and its
/// fingerprint). Read with limits before a byte is trusted: no links, no paths out, no twins, sizes and ratio bounded,
/// every file as listed and none beside them, and the signature good. Whether its key is trusted is the installer's
/// question (Admin → Plugins → Trusted publishers). The framework's conformance packages hold this to its rules.
/// </summary>
public static class PluginFap
{
    public const string FilesJson = "META-INF/files.json";
    public const string SignatureFile = "META-INF/signature";
    public const int MostFiles = 2000;
    public const long MostFileBytes = 20L * 1024 * 1024, MostTotalBytes = 64L * 1024 * 1024;
    public const int MostRatio = 200;

    public sealed class FapException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>Whether the zip is a .fap (it has its file list), not a plugins v2 zip.</summary>
    public static bool Is(ZipArchive zip) => zip.GetEntry(FilesJson) is not null;

    /// <summary>A path a package may hold: relative, forward slashes, no "." or "..", no empty parts, printable.</summary>
    public static bool SafePath(string name) => name.Length is > 0 and <= 512 && !name.StartsWith('/') && !name.Contains('\\') && !name.Any(char.IsControl)
        && !(name.Length > 1 && name[1] == ':') && name.Split('/').All(p => p is not ("" or "." or ".."));

    /// <summary>Every file of the package (META-INF's apart) checked against its list and signature.</summary>
    public static (Dictionary<string, byte[]> Files, PluginSignature Signature) Read(byte[] bytes, IReadOnlyCollection<string>? trustedKeys = null)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > MostFiles)
        {
            throw new FapException("zip.files", $"The package has {zip.Entries.Count} files, more than {MostFiles}.");
        }
        var all = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }
            if (!SafePath(entry.FullName))
            {
                throw new FapException("zip.path", $"{entry.FullName} is outside the package, or not a path it may hold.");
            }
            if (all.ContainsKey(entry.FullName))
            {
                throw new FapException("zip.duplicate", $"{entry.FullName} is in the package twice.");
            }
            // The Unix mode in the external attributes: a link is refused (a package holds files only).
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new FapException("zip.link", $"{entry.FullName} is a link; a package holds files only.");
            }
            if (entry.Length > MostFileBytes)
            {
                throw new FapException("zip.file_size", $"{entry.FullName} is {entry.Length} bytes, more than {MostFileBytes}.");
            }
            total += entry.Length;
            if (total > MostTotalBytes)
            {
                throw new FapException("zip.total_size", $"The package's files are more than {MostTotalBytes} bytes.");
            }
            if (entry.Length > 512 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > MostRatio)
            {
                throw new FapException("zip.ratio", $"{entry.FullName} grows {entry.Length / Math.Max(1, entry.CompressedLength)} times when unpacked, more than {MostRatio}.");
            }
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                copy.Write(buffer, 0, n);
                if (copy.Length > entry.Length)
                {
                    throw new FapException("zip.format", $"{entry.FullName} unpacks larger than it says.");
                }
            }
            all[entry.FullName] = copy.ToArray();
        }
        if (!all.TryGetValue(FilesJson, out var listBytes))
        {
            throw new FapException("package.files.missing", $"The package has no {FilesJson}.");
        }
        JsonObject list;
        try
        {
            list = JsonNode.Parse(listBytes) as JsonObject ?? throw new JsonException();
            if (list["format"]?.GetValue<int>() != 1 || list["files"] is not JsonObject)
            {
                throw new JsonException();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new FapException("package.files.invalid", $"{FilesJson} is not a file list this Arena reads.");
        }
        var listed = (JsonObject)list["files"]!;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, data) in all)
        {
            if (name is FilesJson or SignatureFile)
            {
                continue;
            }
            if (name.StartsWith("META-INF/", StringComparison.Ordinal))
            {
                throw new FapException("package.meta.unknown", $"{name} is not a file a package holds under META-INF.");
            }
            if (listed[name] is not JsonObject want)
            {
                throw new FapException("package.file.unlisted", $"{name} is in the package but not in its file list.");
            }
            if (want["size"]?.GetValue<long>() != data.LongLength || !string.Equals(want["sha256"]?.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(data)), StringComparison.Ordinal))
            {
                throw new FapException("package.file.changed", $"{name} is not the file that was packaged (its SHA-256 differs).");
            }
            files[name] = data;
        }
        if (listed.Select(kv => kv.Key).FirstOrDefault(k => !all.ContainsKey(k)) is { } gone)
        {
            throw new FapException("package.file.gone", $"{gone} is in the file list but not in the package.");
        }
        var signature = new PluginSignature(false, null);
        if (all.TryGetValue(SignatureFile, out var sigBytes))
        {
            string alg, key, sig, pem;
            try
            {
                var s = JsonNode.Parse(sigBytes) as JsonObject ?? throw new JsonException();
                (alg, key, sig, pem) = (s["alg"]!.GetValue<string>(), s["key"]!.GetValue<string>(), s["sig"]!.GetValue<string>(), s["publicKey"]!.GetValue<string>());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException)
            {
                throw new FapException("package.signature.invalid", $"{SignatureFile} is not a signature this Arena reads.");
            }
            if (alg != "ES256")
            {
                throw new FapException("package.signature.alg", $"The signature's algorithm {alg} is not ES256.");
            }
            // A trusted publisher's key by the fingerprint the signature names; else the key the package carries, which must be the one it names.
            using var ecdsa = ECDsa.Create();
            var trustedPem = (trustedKeys ?? []).FirstOrDefault(k => PublisherKey(k).Fingerprint == key);
            try
            {
                ecdsa.ImportFromPem(trustedPem ?? pem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                throw new FapException("package.signature.invalid", "The signature names a key it does not carry.");
            }
            if (ecdsa.KeySize != 256 || Fingerprint(ecdsa) != key)
            {
                throw new FapException("package.signature.invalid", "The signature names a key it does not carry.");
            }
            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(sig);
            }
            catch (FormatException)
            {
                throw new FapException("package.signature.bad", "The signature is not base64.");
            }
            if (!ecdsa.VerifyData(listBytes, raw, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new FapException("package.signature.bad", "The signature does not match the file list: the package was changed after it was signed.");
            }
            signature = new PluginSignature(true, key, trustedPem is not null);
            return (files, signature);
        }
        return (files, signature);
    }

    /// <summary>A public key's fingerprint, as the framework writes it: SHA-256 of its SPKI DER, hex.</summary>
    public static string Fingerprint(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    /// <summary>A trusted publisher's key: an ECDSA P-256 public key in PEM, and its fingerprint; or why not.</summary>
    public static (string? Fingerprint, string? Problem) PublisherKey(string pem)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return ecdsa.KeySize == 256 ? (Fingerprint(ecdsa), null) : (null, "The key is not a P-256 key (arena-plugin keygen makes one).");
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return (null, "That is not an ECDSA public key in PEM (arena-plugin keygen writes arena-plugin.key.pub.pem).");
        }
    }

    /// <summary>The text files among a package's, for plugins v2's reader (the manifest, documents, skills, assistants, rules).</summary>
    public static Dictionary<string, string> Texts(IReadOnlyDictionary<string, byte[]> files) =>
        files.Where(f => IsText(f.Value)).ToDictionary(f => f.Key, f => Encoding.UTF8.GetString(f.Value), StringComparer.Ordinal);

    private static bool IsText(byte[] data)
    {
        if (data.Contains((byte)0))
        {
            return false;
        }
        try
        {
            _ = new UTF8Encoding(false, true).GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
