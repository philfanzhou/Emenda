using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Emenda.Database;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Npgsql;

// Operator-only tool. Credentials arrive on stdin and are never persisted or printed.
try
{
    if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix permissions required");
    if (args.Length != 3 || args[0] is not ("audit" or "preflight" or "queue" or "verify")) throw new InvalidDataException("arguments");
    var mode = args[0];
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    var planBytes = await File.ReadAllBytesAsync(args[1]);
    if (Convert.ToHexString(SHA256.HashData(planBytes)).ToLowerInvariant() != "c808c804b59de0f4f78feaa022a0c31e98dc66e9ee8ee92f66aa12cb3b85d30c")
        throw new InvalidDataException("approved_plan_changed");
    var plan = JsonDocument.Parse(planBytes).RootElement;
    var jobVersion = plan.GetProperty("jobVersions").EnumerateArray().Single();
    var id = jobVersion.GetProperty("Id").GetGuid();
    var version = jobVersion.GetProperty("Version").GetInt32();
    var approved = plan.GetProperty("retirementCandidates").EnumerateArray()
        .ToDictionary(p => p.GetProperty("path").GetString()!, p => p.GetProperty("currentBytes").GetInt64(), StringComparer.Ordinal);
    if (plan.GetProperty("jobs").GetInt32() != 1 || approved.Count != 27 || approved.Values.Sum() != 273274
        || approved.Keys.Any(p => !p.StartsWith($"mistakes/extractions/{id:N}/versions/", StringComparison.Ordinal)))
        throw new InvalidDataException("approved_scope");
    var backup = Path.GetFullPath(args[2]);
    if (backup.StartsWith(Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException("backup_must_be_outside_checkout");
    if (mode == "audit" && Directory.Exists(backup)) throw new InvalidDataException("backup_must_be_new");
    var env = await JsonSerializer.DeserializeAsync<Dictionary<string,string>>(Console.OpenStandardInput()) ?? throw new InvalidDataException("configuration");
    string Required(string name) => env.TryGetValue(name,out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("configuration");
    var connection = new NpgsqlConnectionStringBuilder(Required("ConnectionStrings__Emenda")) { Host = "192.168.31.5", Timeout = 10, CommandTimeout = 30 };
    var endpoint = new Uri("http://" + Required("Oss__InternalEndpoint"));
    using var s3 = new MinioClient().WithEndpoint("192.168.31.5", endpoint.Port)
        .WithCredentials(Required("Oss__AccessKey"),Required("Oss__SecretKey"))
        .WithSSL(bool.Parse(Required("Oss__InternalSecure"))).Build();
    var bucket = Required("Oss__BucketName");
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var ct = deadline.Token;
    await using var db = new MistakeDbContext(new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connection.ConnectionString).Options);
    await using var tx = await db.Database.BeginTransactionAsync(mode == "queue" ? IsolationLevel.Serializable : IsolationLevel.RepeatableRead, ct);
    if (mode != "queue") await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
    else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString())),0)})", ct);
    if (await db.ExtractionJobs.CountAsync(ct) != 1) throw new InvalidDataException("job_scope_changed");
    var job = await db.ExtractionJobs.SingleAsync(j => j.Id == id, ct);
    if (job.Version != version || job.Status != "Succeeded") throw new InvalidDataException("job_version_changed");
    var sources = JsonSerializer.Deserialize<List<ExtractionSource>>(job.SourcesJson,json)!;
    var revision = await db.ExtractionRevisions.SingleAsync(r => r.JobId == id && r.Version == version,ct);
    var snapshot = JsonSerializer.Deserialize<ExtractionSnapshot>(revision.SnapshotJson,json)!;
    var current = snapshot.Questions.Where(q => q.Question.Outcome == "incorrect" && !q.ItemDeleted)
        .SelectMany(q => q.Images).ToDictionary(i => i.Path,i => i.Sha256,StringComparer.Ordinal);
    var protectedPaths = sources.Select(s => s.Path).Concat(current.Keys).ToHashSet(StringComparer.Ordinal);
    if (sources.Count != 30 || current.Count != 3 || approved.Keys.Any(protectedPaths.Contains)) throw new InvalidDataException("protected_scope_changed");
    var linked = new HashSet<string>(StringComparer.Ordinal);
    await foreach (var item in db.MistakeItems.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct)) linked.UnionWith(item.SourceRegions.Select(r => r.SourceImagePath));
    if (approved.Keys.Any(linked.Contains)) throw new InvalidDataException("candidate_is_referenced");
    var items = await db.MistakeItems.AsNoTracking().OrderBy(i=>i.Id)
        .Select(i=>new { i.Id,i.StudentId,i.Subject,i.Grade,i.SourceUploadId,i.QuestionId,i.ReviewStatus,i.RootCause,i.ReviewerId,i.ReviewedAt,i.CreatedAt,i.UpdatedAt }).ToListAsync(ct);
    var preservedHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { items,questions=snapshot.Questions.Select(q=>q.Question).ToList() },json))).ToLowerInvariant();
    var objects = new Dictionary<string,long>(StringComparer.Ordinal);
    await foreach (var item in s3.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(bucket).WithPrefix($"mistakes/extractions/{id:N}/").WithRecursive(true),ct)) objects.Add(item.Key,checked((long)item.Size));
    if (mode != "verify")
    {
        var versions = new Dictionary<string,List<(string Id,long Bytes)>>(StringComparer.Ordinal);
        await foreach (var item in s3.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(bucket).WithPrefix($"mistakes/extractions/{id:N}/").WithRecursive(true).WithVersions(true),ct))
        {
            // Reject hidden historical paths, including thumbnails that normal cleanup also derives.
            if (!objects.ContainsKey(item.Key)) throw new InvalidDataException("unapproved_historical_path");
            if (!versions.TryGetValue(item.Key,out var list)) versions[item.Key] = list = [];
            list.Add((item.VersionId ?? "null",checked((long)item.Size)));
        }
        foreach (var candidate in plan.GetProperty("retirementCandidates").EnumerateArray())
        {
            var path = candidate.GetProperty("path").GetString()!;
            var expectedVersions = candidate.GetProperty("contentVersions").EnumerateArray()
                .Select(v=>(Id:v.GetProperty("version").GetString()!,Bytes:v.GetProperty("bytes").GetInt64())).OrderBy(v=>v.Id).ToArray();
            if (!versions.TryGetValue(path,out var actual) || !actual.OrderBy(v=>v.Id).SequenceEqual(expectedVersions))
                throw new InvalidDataException("candidate_versions_changed");
        }
    }
    var hashes = new Dictionary<string,string>(StringComparer.Ordinal);
    if (mode == "audit")
    {
        Directory.CreateDirectory(backup, UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        if (!objects.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(protectedPaths.Concat(approved.Keys))) throw new InvalidDataException("object_scope_changed");
    }
    var expected = sources.ToDictionary(s=>s.Path,s=>s.Sha256,StringComparer.Ordinal);
    foreach (var pair in current) expected.Add(pair.Key,pair.Value);
    foreach (var path in mode != "verify" ? protectedPaths.Concat(approved.Keys) : protectedPaths)
    {
        using var memory = new MemoryStream();
        await s3.GetObjectAsync(new GetObjectArgs().WithBucket(bucket).WithObject(path).WithCallbackStream(stream =>
        {
            var buffer = new byte[81920]; int n;
            while ((n=stream.Read(buffer,0,buffer.Length))>0)
            { if (memory.Length+n>64*1024*1024) throw new InvalidDataException("object_size"); memory.Write(buffer,0,n); }
        }),ct);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expected.TryGetValue(path,out var known) && known != hash) throw new InvalidDataException("protected_hash_mismatch");
        if (approved.TryGetValue(path,out var size) && size!=bytes.Length) throw new InvalidDataException("candidate_length_mismatch");
        hashes.Add(path,hash);
        if (mode == "audit")
        {
            var file = Path.Combine(backup,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()+".bin");
            await File.WriteAllBytesAsync(file,bytes,ct);
            File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        }
    }
    var metadataFile = Path.Combine(backup,"manifest.json");
    if (mode == "audit")
    {
        await File.WriteAllTextAsync(metadataFile,JsonSerializer.Serialize(new { id,version,hashes,protectedPaths,approved,preservedHash,job.SourcesJson,revision.SnapshotJson },json),ct);
        File.SetUnixFileMode(metadataFile,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    else
    {
        var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(metadataFile,ct)).RootElement;
        if (baseline.GetProperty("preservedHash").GetString()!=preservedHash) throw new InvalidDataException("items_or_reviews_changed");
        foreach (var pair in hashes) if (baseline.GetProperty("hashes").GetProperty(pair.Key).GetString()!=pair.Value) throw new InvalidDataException("backup_hash_mismatch");
        foreach (var saved in baseline.GetProperty("hashes").EnumerateObject())
        {
            var file = Path.Combine(backup,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(saved.Name))).ToLowerInvariant()+".bin");
            var size = new FileInfo(file).Length;
            if (size>64*1024*1024 || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file,ct))).ToLowerInvariant()!=saved.Value.GetString())
                throw new InvalidDataException("backup_bytes_mismatch");
        }
        if (mode != "preflight" && !(await db.Database.GetAppliedMigrationsAsync(ct)).Contains(ExtractionCorrectionsSchema.Migration)) throw new InvalidDataException("schema20_required");
        if (mode != "preflight" && snapshot.Questions.Where(q=>q.Question.Outcome!="incorrect" || q.ItemDeleted).Any(q=>q.Images.Count!=0)) throw new InvalidDataException("invalid_current_geometry");
        if (mode == "queue")
        {
            if (approved.Any(p=>!objects.TryGetValue(p.Key,out var length)||length!=p.Value)) throw new InvalidDataException("candidate_object_changed");
            var paths = approved.Keys.ToArray();
            var rows = await db.ExtractionObjects.Where(o=>o.JobId==id && paths.Contains(o.Path)).ToListAsync(ct);
            if (rows.Count!=27 || rows.Any(o=>!o.Legacy || !o.Pending)) throw new InvalidDataException("queue_scope_changed");
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE extraction_object SET legacy=false,next_attempt=clock_timestamp() WHERE job_id={id} AND path=ANY({paths}) AND legacy AND pending",ct);
            if (affected!=27) throw new InvalidDataException("queue_count_changed");
        }
        else if (mode == "verify")
        {
            if (!objects.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(protectedPaths)) throw new InvalidDataException("cleanup_incomplete");
            await foreach (var item in s3.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(bucket).WithPrefix($"mistakes/extractions/{id:N}/").WithRecursive(true).WithVersions(true),ct))
                if (approved.ContainsKey(item.Key)) throw new InvalidDataException("old_content_version_retained");
            var paths = approved.Keys.ToArray();
            if (await db.ExtractionObjects.AnyAsync(o=>paths.Contains(o.Path),ct)) throw new InvalidDataException("queue_incomplete");
            if (approved.Keys.Any(revision.SnapshotJson.Contains)) throw new InvalidDataException("old_path_retained");
        }
    }
    await tx.CommitAsync(ct);
    Console.WriteLine(JsonSerializer.Serialize(new { mode,passed=true,jobId=id,version,originals=sources.Count,currentCrops=current.Count,
        protectedHashesVerified=33,approvedObjects=approved.Count,approvedBytes=approved.Values.Sum(),s3Objects=objects.Count,s3Bytes=objects.Values.Sum(),
        preservedItems=items.Count,preservedHash,
        database=connection.Database,databaseUser=connection.Username,backupDirectory=backup },json));
}
catch (Exception error)
{
    Console.Error.WriteLine("Approved migration check failed: "+(error is InvalidDataException ? error.Message : error.GetType().Name));
    Environment.ExitCode=1;
}
