using System.Security.Cryptography;

namespace LeadManager.Api.Services;

/// <summary>
/// Stores uploaded documents on disk under one root. Names on disk are generated; the name the user gave is only
/// metadata, so a file name can never point outside the root. The root is a plain folder inside the data volume,
/// which the backup script already archives.
/// </summary>
public class DocumentStorage
{
    public const long MaxFileBytes = 25L * 1024 * 1024;
    public const int MaxFilesPerDocument = 20;

    // Extension decides the type we serve; the type the browser claims is ignored.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".txt"] = "text/plain",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".eml"] = "message/rfc822",
        [".msg"] = "application/vnd.ms-outlook",
    };

    private readonly string _root;

    public DocumentStorage(IConfiguration config)
    {
        var configured = config["Documents:Path"];
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "documents")
            : configured));
    }

    public static string AllowedExtensions => string.Join(", ", Allowed.Keys);

    /// <summary>Content type for an allowed file name, or null when the type is not accepted.</summary>
    public static string? ContentTypeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        return Allowed.TryGetValue(ext, out var type) ? type : null;
    }

    /// <summary>The name as it is shown: no path, no control characters, at most 200 characters.</summary>
    public static string CleanName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)
            && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        if (name.Length > 200) name = name[^200..];
        return name.Length == 0 ? "document" : name;
    }

    public async Task<(string storagePath, long size, string sha256)> SaveAsync(Stream content, CancellationToken ct)
    {
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy"), Guid.NewGuid().ToString("N"));
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        long size = 0;
        using var hash = SHA256.Create();
        try
        {
            await using var target = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                size += read;
                if (size > MaxFileBytes) throw new InvalidDataException("File is too large");
                hash.TransformBlock(buffer, 0, read, null, 0);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }
        catch
        {
            TryDelete(relative);
            throw;
        }
        return (relative, size, Convert.ToHexString(hash.Hash!).ToLowerInvariant());
    }

    public Stream? Open(string storagePath)
    {
        var full = Resolve(storagePath);
        return File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void TryDelete(string storagePath)
    {
        try
        {
            var full = Resolve(storagePath);
            if (File.Exists(full)) File.Delete(full);
        }
        catch (IOException) { /* the row is what matters; an orphan file is harmless */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Joins to the root and refuses anything that ends up outside it.</summary>
    private string Resolve(string storagePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, storagePath));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes the documents root");
        return full;
    }
}
