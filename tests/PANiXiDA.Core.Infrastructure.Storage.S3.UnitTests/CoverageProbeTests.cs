using System.IO.Compression;
using System.Xml.Linq;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.UnitTests;

public sealed class CoverageProbeTests
{
    [Fact(DisplayName = "Temporary coverage artifact inspection")]
    public async Task InspectCoverage()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var bytes = await client.GetByteArrayAsync(
            "https://nightly.link/panixida-dotnet-core/infrastructure-storage-s3/actions/artifacts/14698562912.zip",
            TestContext.Current.CancellationToken);
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.Entries.Single(item => item.Name == "SonarQube.xml");
        await using var xml = entry.Open();
        var report = await XDocument.LoadAsync(xml, LoadOptions.None, TestContext.Current.CancellationToken);
        var uncovered = report.Descendants("file").SelectMany(file => file.Elements("lineToCover")
            .Where(line => (string?)line.Attribute("covered") == "false"
                || ((int?)line.Attribute("coveredBranches") ?? 0) < ((int?)line.Attribute("branchesToCover") ?? 0))
            .Select(line => $"{file.Attribute("path")}: {line}"));

        Assert.Fail(string.Join(Environment.NewLine, uncovered));
    }
}
