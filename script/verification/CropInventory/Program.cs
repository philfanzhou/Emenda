using System.Data;
using System.Text.Json;
using Emenda.Database;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Npgsql;

// Input is an environment-name/value JSON object on stdin; never print it or exception details.
try
{
    var env = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(Console.OpenStandardInput())
        ?? throw new InvalidDataException();
    string Required(string key) => env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidDataException();
    var connection = new NpgsqlConnectionStringBuilder(Required("ConnectionStrings__Emenda"));
    var endpoint = Required("Oss__InternalEndpoint");
    // Explicit override for published test-host ports; no changes to the service configuration.
    if (args.Length == 2 && args[0] == "--host")
    {
        connection.Host = args[1];
        endpoint = args[1] + ":" + new Uri("http://" + endpoint).Port;
    }
    else if (args.Length != 0) throw new InvalidDataException();
    connection.Timeout = 10;
    connection.CommandTimeout = 30;
    var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connection.ConnectionString).Options;
    await using var db = new MistakeDbContext(options);
    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
    await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
    var jobs = await db.ExtractionJobs.AsNoTracking().Select(j => new { j.Id, j.Version, j.SourcesJson }).ToListAsync();
    var versions = jobs.ToDictionary(j => j.Id, j => j.Version);
    var originals = new HashSet<string>(StringComparer.Ordinal);
    var current = new HashSet<string>(StringComparer.Ordinal);
    var historical = new HashSet<string>(StringComparer.Ordinal);
    var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    foreach (var job in jobs)
    foreach (var source in JsonSerializer.Deserialize<List<ExtractionSource>>(job.SourcesJson, json) ?? throw new InvalidDataException())
    {
        originals.Add(source.Path);
        lengths[source.Path] = source.Length;
    }
    var revisions = await db.ExtractionRevisions.AsNoTracking().Select(r => new { r.JobId, r.Version, r.SnapshotJson }).ToListAsync();
    foreach (var revision in revisions)
    foreach (var question in (JsonSerializer.Deserialize<ExtractionSnapshot>(revision.SnapshotJson, json) ?? throw new InvalidDataException()).Questions)
    foreach (var image in question.Images)
    {
        (versions[revision.JobId] == revision.Version ? current : historical).Add(image.Path);
        lengths[image.Path] = image.Length;
    }
    var live = new HashSet<string>(StringComparer.Ordinal);
    await foreach (var item in db.MistakeItems.AsNoTracking().AsAsyncEnumerable())
    foreach (var region in item.SourceRegions)
        live.Add(region.SourceImagePath);
    await transaction.CommitAsync();

    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    using var s3 = new MinioClient().WithEndpoint(endpoint)
        .WithCredentials(Required("Oss__AccessKey"), Required("Oss__SecretKey"))
        .WithSSL(bool.Parse(Required("Oss__InternalSecure"))).Build();
    var objects = new Dictionary<string, long>(StringComparer.Ordinal);
    // Use the installed SDK directly: IOssService.ListObjectsAsync currently swallows listing failures.
    await foreach (var item in s3.ListObjectsEnumAsync(new ListObjectsArgs()
        .WithBucket(Required("Oss__BucketName")).WithPrefix("mistakes/extractions/").WithRecursive(true), deadline.Token))
    {
        if (objects.Count >= 1000000) throw new InvalidDataException();
        objects.Add(item.Key, checked((long)item.Size));
    }
    object Summary(IEnumerable<string> paths)
    {
        var keys = paths.Distinct(StringComparer.Ordinal).ToList();
        return new { objects = keys.Count, present = keys.Count(objects.ContainsKey),
            storedBytes = keys.Where(objects.ContainsKey).Sum(k => objects[k]),
            missing = keys.Count(k => !objects.ContainsKey(k)),
            lengthMismatch = keys.Count(k => objects.TryGetValue(k, out var size) && lengths.TryGetValue(k, out var expected) && size != expected) };
    }
    var objectVersions = new List<(string Path, string Version, long Bytes)>();
    await foreach (var item in s3.ListObjectsEnumAsync(new ListObjectsArgs()
        .WithBucket(Required("Oss__BucketName")).WithPrefix("mistakes/extractions/").WithRecursive(true).WithVersions(true), deadline.Token))
    {
        if (objectVersions.Count >= 1000000) throw new InvalidDataException();
        objectVersions.Add((item.Key, item.VersionId ?? "null", checked((long)item.Size)));
    }
    var candidates = current.Concat(historical).Except(originals).Except(live)
        .Where(p => p.StartsWith("mistakes/extractions/", StringComparison.Ordinal) && p.Contains("/versions/", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    var retained = originals.Concat(current).Concat(historical).Concat(live).ToHashSet(StringComparer.Ordinal);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        observedAtUtc = DateTimeOffset.UtcNow, readOnly = true, jobs = jobs.Count, revisions = revisions.Count,
        s3Objects = objects.Count, s3Bytes = objects.Values.Sum(), originals = Summary(originals),
        currentCrops = Summary(current), historyOnlyCrops = Summary(historical.Except(current).Except(live)),
        currentMistakeCrops = Summary(live.Where(p => p.StartsWith("mistakes/extractions/", StringComparison.Ordinal))),
        outsideManifestsNeedsReview = Summary(objects.Keys.Except(retained)),
        prospectiveCurrentOnlyRetirement = Summary(candidates),
        retirementCandidates = candidates.Select(path => new { path, currentBytes = objects.GetValueOrDefault(path),
            contentVersions = objectVersions.Where(v => v.Path == path).Select(v => new { version = v.Version, bytes = v.Bytes }).ToList() }),
        jobVersions = jobs.Select(j => new { j.Id, j.Version }),
        deletionApproved = false, objectVersionsInspected = true,
        limitations = "Database and S3 are sequential observations, not an atomic snapshot. Candidates predict current-only migration and are not approved for deletion. Verify unchanged job versions, current sources/references and object fingerprints before acting. Zero-byte delete markers are not image content."
    }));
}
catch (Exception error)
{
    Console.Error.WriteLine("Inventory failed (" + error.GetType().Name + "); no complete report produced. Inspect privately; credentials and source records are not printed.");
    Environment.ExitCode = 1;
}
