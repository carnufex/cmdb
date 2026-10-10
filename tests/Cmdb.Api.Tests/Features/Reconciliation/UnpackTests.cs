using System.IO.Compression;
using Cmdb.Api.Features.Reconciliation;

namespace Cmdb.Api.Tests.Features.Reconciliation;

/// <summary>The archive a reconciliation (#216) is sent as: the exchange format's files, nothing else.</summary>
public sealed class UnpackTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-unpack-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task The_formats_files_are_unpacked_from_any_folder_of_the_archive()
    {
        (await Reconciliations.UnpackAsync(Zip(("export/sites.csv", "id\n"), ("export/equipment.csv", "id\n")), _folder, Ct)).ShouldBeNull();

        Directory.GetFiles(_folder).Select(Path.GetFileName).Order().ShouldBe(["equipment.csv", "sites.csv"]);
    }

    [Theory]
    [InlineData("../sites.csv.sh")]
    [InlineData("notes.txt")]
    public async Task Other_files_are_refused(string name)
    {
        (await Reconciliations.UnpackAsync(Zip(("sites.csv", "id\n"), (name, "x")), _folder, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_file_twice_or_none_at_all_is_refused()
    {
        (await Reconciliations.UnpackAsync(Zip(("a/sites.csv", "id\n"), ("b/sites.csv", "id\n")), _folder, Ct)).ShouldNotBeNull();
        (await Reconciliations.UnpackAsync(Zip(), _folder, Ct)).ShouldNotBeNull();
        (await Reconciliations.UnpackAsync(new MemoryStream([1, 2, 3]), _folder, Ct)).ShouldBe("Filen är inget zip-arkiv.");
    }

    private static MemoryStream Zip(params (string Name, string Text)[] files)
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        zip.Position = 0;
        return zip;
    }
}
