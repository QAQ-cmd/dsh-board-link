using System.IO.Compression;
using System.Reflection;

namespace DshLink;

/// <summary>Builds a downloadable copy of the DSH plugin from embedded resources.</summary>
public static class PluginPackage
{
    private static readonly (string Resource, string Entry)[] Files =
    {
        ("DshLink.Plugin.package.json", "dsh-board-link/package.json"),
        ("DshLink.Plugin.cordis.patch.yml", "dsh-board-link/cordis.patch.yml"),
        ("DshLink.Plugin.lib.index.js", "dsh-board-link/lib/index.js"),
        ("DshLink.Plugin.lib.client.js", "dsh-board-link/lib/client.js"),
        ("DshLink.Plugin.lib.uiclient.js", "dsh-board-link/lib/ui-client.js"),
        ("DshLink.Plugin.README.md", "dsh-board-link/README.md"),
        ("DshLink.Plugin.README.zh.md", "dsh-board-link/README.zh.md"),
        ("DshLink.Plugin.LICENSE", "dsh-board-link/LICENSE"),
    };

    public static byte[] CreateZip()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (resource, entryName) in Files)
            {
                using var source = assembly.GetManifestResourceStream(resource)
                                   ?? throw new InvalidOperationException(
                                       $"embedded plugin resource missing: {resource}; available: {string.Join(", ", assembly.GetManifestResourceNames())}");
                var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        return memory.ToArray();
    }
}
