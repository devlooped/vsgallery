using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Octokit;
using Octokit.Internal;

/// <summary>
/// Custom gallery implementation.
/// </summary>
[StorageAccount("AZURE_STORAGE")]
public static class VisualStudioGallery
{
    const string FeedId = "ExtensionGallery";
    const string FeedTitle = "Extension Gallery";

    static XNamespace AtomNs => XNamespace.Get("http://www.w3.org/2005/Atom");
    static XNamespace GalleryNs => XNamespace.Get("http://schemas.microsoft.com/developer/vsx-syndication-schema/2010");
    static XNamespace VsixNs => XNamespace.Get("http://schemas.microsoft.com/developer/vsx-schema/2011");

    /// <summary>
    /// Blob-triggered function that updates the feed.
    /// </summary>
    [FunctionName(nameof(Update))]
    public static async Task Update(
        [BlobTrigger(ThisAssembly.Project.AZURE_CONTAINER + "/{name}.vsix")] Stream blob,
        Uri uri,
        string name,
        [Blob(ThisAssembly.Project.AZURE_CONTAINER + "/atom.xml", FileAccess.Read)]
        Stream? currentFeed,
        [Blob(ThisAssembly.Project.AZURE_CONTAINER + "/atom.xml", FileAccess.Write)]
        Stream updatedFeed,
        [Blob(ThisAssembly.Project.AZURE_CONTAINER + "/{name}.png", FileAccess.Write)]
        Stream icon,
        ILogger log)
    {
        var storageBaseUrl = string.Join('/', uri.AbsoluteUri.Split('/')[..^1]) + "/";

        XElement atom;
        if (currentFeed == null)
        {
            atom = new XElement(AtomNs + "feed",
                new XElement(AtomNs + "title", new XAttribute("type", "text"), FeedTitle),
                new XElement(AtomNs + "id", FeedId)
            );
            log.LogInformation("Created brand new feed.");
        }
        else
        {
            try
            {
                atom = XDocument.Load(currentFeed).Root!;
                atom.Element(AtomNs + "title")?.SetValue(FeedTitle);
                atom.Element(AtomNs + "id")?.SetValue(FeedId);
            }
            catch (XmlException xe)
            {
                log.LogWarning(xe, "Failed to load feed, replacing with blank one.");
                // Auto-overwrite poison feed content too.
                atom = new XElement(AtomNs + "feed",
                    new XElement(AtomNs + "title", new XAttribute("type", "text"), FeedTitle),
                    new XElement(AtomNs + "id", FeedId)
                );
            }
        }

        var updated = atom.Element(AtomNs + "updated");
        if (updated == null)
        {
            updated = new XElement(AtomNs + "updated");
            atom.Add(updated);
        }

        updated.Value = XmlConvert.ToString(DateTimeOffset.UtcNow);

        using var archive = new ZipArchive(blob, ZipArchiveMode.Read);
        var zipEntry = archive.GetEntry("extension.vsixmanifest");
        if (zipEntry == null)
        {
            log.LogWarning("Could not find extension.vsixmanifest in archive {0}.vsix", name);
            return;
        }

        using var stream = zipEntry.Open();

        if (XDocument.Load(stream).Root is not XElement manifest ||
            manifest.Element(VsixNs + "Metadata") is not XElement metadata ||
            metadata.Element(VsixNs + "Identity") is not XElement identity ||
            identity.Attribute("Id")?.Value is not string id ||
            identity.Attribute("Version")?.Value is not string version)
            return;

        var entry = atom.Elements(AtomNs + "entry").FirstOrDefault(x => x.Element(AtomNs + "id")?.Value == id);
        if (entry != null)
            entry.Remove();

        entry = new XElement(AtomNs + "entry",
            new XElement(AtomNs + "id", id),
            new XElement(AtomNs + "title", new XAttribute("type", "text"), metadata.Element(VsixNs + "DisplayName")?.Value ?? ""),
            new XElement(AtomNs + "link",
                new XAttribute("rel", "alternate"),
                new XAttribute("href", $"{storageBaseUrl}{name}.vsix")),
            new XElement(AtomNs + "summary", new XAttribute("type", "text"), metadata.Element(VsixNs + "Description")?.Value ?? ""),
            new XElement(AtomNs + "published", XmlConvert.ToString(DateTimeOffset.UtcNow)),
            new XElement(AtomNs + "updated", XmlConvert.ToString(DateTimeOffset.UtcNow)),
            new XElement(AtomNs + "author",
                new XElement(AtomNs + "name", identity.Attribute("Publisher")?.Value ?? "")),
            new XElement(AtomNs + "content",
                new XAttribute("type", "application/octet-stream"),
                new XAttribute("src", $"{storageBaseUrl}{name}.vsix"))
        );

        if (metadata.Element(VsixNs + "Icon") is XElement iconElement)
        {
            try
            {
                var iconEntry = archive.GetEntry(iconElement.Value.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (iconEntry != null)
                {
                    using (var iconStream = iconEntry.Open())
                        iconStream.CopyTo(icon);

                    entry.Add(new XElement(AtomNs + "link",
                        new XAttribute("rel", "icon"),
                        new XAttribute("href", $"{storageBaseUrl}{name}.png")));
                }
            }
            catch { }
        }

        var vsix = new XElement(GalleryNs + "Vsix",
            new XElement(GalleryNs + "Id", id),
            new XElement(GalleryNs + "Version", version),
            new XElement(GalleryNs + "References")
        );

        entry.Add(vsix);
        atom.AddFirst(entry);

        using var writer = XmlWriter.Create(updatedFeed, new XmlWriterSettings { CloseOutput = false, Indent = true });
        atom.WriteTo(writer);
        writer.Flush();

        log.LogInformation("Successfully updated feed with {0} {1}.", id, version);

        // Optionally update commit by reporting the feed update status.
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            var jsonEntry = archive.GetEntry("source.json");
            if (jsonEntry != null)
            {
                using var jsStream = jsonEntry.Open();
                using var jsReader = new StreamReader(jsStream);
                var source = JsonSerializer.Deserialize<Source>(jsStream, new JsonSerializerOptions { AllowTrailingCommas = true });
                var repo = source?.repository;
                if (string.IsNullOrEmpty(repo))
                    repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");

                if (source != null && !string.IsNullOrEmpty(repo) && repo.Split('/') is string[] parts && parts.Length == 2)
                {
                    var client = new GitHubClient(new ProductHeaderValue("vsgallery"), new InMemoryCredentialStore(new Credentials(token, AuthenticationType.Bearer)));
                    var kind = storageBaseUrl.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
                    var status = await client.Repository.Status.Create(parts[0], parts[1], source.commit, new NewCommitStatus
                    {
                        Context = "feed-" + kind,
                        State = CommitState.Success,
                        TargetUrl = $"{storageBaseUrl.TrimEnd('/')}/atom.xml",
                        Description = $"Successfully published to {kind} gallery feed"
                    });

                    log.LogInformation("Successfully reported status for {0}@{1}.", repo, source.commit);
                }
                else
                {
                    log.LogWarning("Could not determine GitHub repository name.");
                }
            }
            else
            {
                log.LogWarning("Could not find source.json in archive {0}.vsix", name);
            }
        }
    }
}

record Source(string commit, string? repository = default);
