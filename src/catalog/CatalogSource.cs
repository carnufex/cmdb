using System.Reflection;

namespace Cmdb.Catalog;

/// <summary>
/// Where the catalog files come from (#207): the synthetic catalog embedded in this build, or a folder with the same
/// layout as <c>catalog/</c> named by <c>CMDB_CATALOG_PATH</c>. Another organisation's models then live outside the
/// repository and change without a release. Validation is the same whatever the source.
/// </summary>
public abstract class CatalogSource
{
    public const string PathVariable = "CMDB_CATALOG_PATH";

    private static readonly Lazy<CatalogSource> CurrentSource = new(() => FromPath(Environment.GetEnvironmentVariable(PathVariable)));

    /// <summary>The catalog this process uses, chosen once: the folder in <c>CMDB_CATALOG_PATH</c>, else the embedded one.</summary>
    public static CatalogSource Current => CurrentSource.Value;

    /// <summary>The synthetic catalog shipped with this build.</summary>
    public static CatalogSource Embedded { get; } = new EmbeddedSource(typeof(CatalogSource).Assembly);

    /// <summary>The folder at <paramref name="path"/>, or the embedded catalog when it is empty.</summary>
    public static CatalogSource FromPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Embedded : FromDirectory(path);

    public static CatalogSource FromDirectory(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return Directory.Exists(full)
            ? new DirectorySource(full)
            : throw new InvalidOperationException($"{PathVariable}={path}: the catalog folder does not exist.");
    }

    /// <summary>Names the source in error messages: <c>embedded</c> or the folder.</summary>
    public abstract string Name { get; }

    /// <summary>The <c>.json</c> files in <paramref name="folder"/> (e.g. <c>equipment-types</c>) by file name, in ordinal order.
    /// A missing folder has no files.</summary>
    public abstract IReadOnlyList<(string File, string Json)> Files(string folder);

    /// <summary>The file at <paramref name="file"/> (e.g. <c>cable-types.json</c>), or null when it is missing.</summary>
    public abstract string? Read(string file);

    /// <summary>
    /// The panel image <paramref name="file"/> in <c>equipment-images/</c> (#214), or null when it is missing or the name is
    /// not a plain image file name.
    /// </summary>
    public byte[]? Image(string file) => CatalogRules.ImageFile().IsMatch(file) ? ReadBytes(ImageFolder + "/" + file) : null;

    public const string ImageFolder = "equipment-images";

    protected abstract byte[]? ReadBytes(string file);

    public override string ToString() => Name;

    private sealed class EmbeddedSource(Assembly assembly) : CatalogSource
    {
        public override string Name => "embedded";

        public override IReadOnlyList<(string File, string Json)> Files(string folder)
        {
            var prefix = folder + "/";
            return [.. assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(n => (n[prefix.Length..], Read(n)!))];
        }

        public override string? Read(string file)
        {
            using var stream = assembly.GetManifestResourceStream(file);
            if (stream is null)
            {
                return null;
            }
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        protected override byte[]? ReadBytes(string file)
        {
            using var stream = assembly.GetManifestResourceStream(file);
            if (stream is null)
            {
                return null;
            }
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    private sealed class DirectorySource(string root) : CatalogSource
    {
        public override string Name => root;

        public override IReadOnlyList<(string File, string Json)> Files(string folder)
        {
            var path = System.IO.Path.Combine(root, folder);
            return Directory.Exists(path)
                ? [.. Directory.EnumerateFiles(path, "*.json")
                    .Select(System.IO.Path.GetFileName)
                    .Order(StringComparer.Ordinal)
                    .Select(n => (n!, File.ReadAllText(System.IO.Path.Combine(path, n!))))]
                : [];
        }

        public override string? Read(string file)
        {
            var path = System.IO.Path.Combine(root, file);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        protected override byte[]? ReadBytes(string file)
        {
            var path = System.IO.Path.Combine(root, file);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
