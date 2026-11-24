using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Emenda.Domain.Models;
using Emenda.HttpContracts;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Emenda.Service.Tests;

public sealed class ExtractionQuestionRevisionHttpPostgresTests
{
    [Fact]
    public async Task AddingMissedQuestion33_RequiresOnlyOneConfirmationAndReplaysWithoutDuplicates()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = Caller(factory);
        var job = await SeedAsync(factory);
        var questions = job.Questions.Select(q => q.Question).Concat(Enumerable.Range(3, 30).Select(n =>
            job.Questions[0].Question with { Id = Guid.NewGuid(), QuestionNumber = n.ToString(),
                Statement = $"Question {n}", Outcome = "correct", StudentAnswer = "2" })).ToList();
        var route = $"/api/intake/extractions/{job.Id}/questions";
        var baseline = await DataAsync<ExtractionJobView>(await caller.PutAsJsonAsync(route,
            new ExtractionRevisionRequest(Guid.NewGuid(), job.Version, "32 question baseline", questions)));
        var missed = questions[0] with { Id = Guid.NewGuid(), QuestionNumber = "33", Statement = "Missed question 33" };
        var request = new ExtractionRevisionRequest(Guid.NewGuid(), baseline.Version, "Add missed question 33", [.. questions, missed]);
        var added = await DataAsync<ExtractionJobView>(await caller.PutAsJsonAsync(route, request));
        var item = added.Questions.Single(q => q.Question.Id == missed.Id).ItemId!.Value;
        Assert.Equal(33, added.Questions.Count);
        Assert.Equal(baseline.Questions.Select(q => (q.Question.Id, q.ItemId)),
            added.Questions.Take(32).Select(q => (q.Question.Id, q.ItemId)));
        using var reviewed = await caller.PostAsJsonAsync($"{route}/{missed.Id}/review",
            new ExtractionReviewRequest(added.Version, item, (int)ReviewStatus.Confirmed, "teacher", null, null));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var replay = await DataAsync<ExtractionJobView>(await caller.PutAsJsonAsync(route, request));
        Assert.Equal(added.Version, replay.Version);
        Assert.Equal(item, replay.Questions.Single(q => q.Question.Id == missed.Id).ItemId);
        await using var db = database.CreateContext();
        Assert.Equal((int)ReviewStatus.Confirmed, (await db.MistakeItems.SingleAsync(i => i.Id == item)).ReviewStatus);
        Assert.Equal(3, await db.MistakeItems.CountAsync());
        var retiredSnapshot = """{"questions":[]}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE extraction_revision SET snapshot_json={retiredSnapshot}::jsonb
            WHERE job_id={job.Id} AND version={added.Version}
            """);
        var archived = await DataAsync<JsonElement>(await caller.GetAsync($"/api/mistakes/{item}"));
        Assert.Equal((int)ReviewStatus.Confirmed, archived.GetProperty("reviewStatus").GetInt32());
        AssertAnalysis(archived.GetProperty("analysisSnapshot"), "Missed question 33", "3", "2",
            "Add one and one", "Actual attempt is 3 rather than 2", added.Version);
    }

    [Fact]
    public async Task GroupedResult_ExposesOnlyMistakes_AndLegacyCorrectionKeepsKnownGrades()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = Caller(factory);
        var job = await SeedAsync(factory);
        var first = job.Questions[0].Question with { Grade = 6 };
        var second = job.Questions[1].Question with { Subject = first.Subject, Grade = 8 };
        var correct = first with { Id = Guid.NewGuid(), Statement = "Answered correctly", Outcome = "correct" };
        var unknown = first with { Id = Guid.NewGuid(), Statement = "No student answer", Outcome = "unknown", StudentAnswer = null };
        var root = $"/api/intake/extractions/{job.Id}";
        var revised = await DataAsync<ExtractionJobView>(await caller.PutAsJsonAsync(root + "/questions",
            new ExtractionRevisionRequest(Guid.NewGuid(), job.Version, "Mixed grades", [first, correct, unknown, second])));
        var legacy = first with { Grade = null, Explanation = "Old Portal correction" };
        var corrected = await DataAsync<ExtractionQuestionRevisionView>(await caller.PutAsJsonAsync(root + $"/questions/{first.Id}",
            new ExtractionQuestionRevisionRequest(Guid.NewGuid(), revised.Version, "No grade in old contract", legacy)));
        Assert.Equal(6, corrected.Question!.Question.Grade);
        var response = await DataAsync<JsonElement>(await caller.GetAsync(root + "/summary"));
        var groups = response.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(new[] { 6, 8 }, groups.Select(g => g.GetProperty("summary").GetProperty("grade").GetInt32()));
        Assert.Equal(3, groups[0].GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(1, groups[0].GetProperty("summary").GetProperty("incorrect").GetInt32());
        var onlyMistake = Assert.Single(groups[0].GetProperty("mistakes").EnumerateArray());
        Assert.Equal(first.Id, onlyMistake.GetProperty("questionId").GetGuid());
        var image = Assert.Single(onlyMistake.GetProperty("images").EnumerateArray()).GetString();
        Assert.Equal(HttpStatusCode.OK, (await caller.GetAsync(image)).StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(6, (await db.MistakeItems.SingleAsync(i => i.Id == corrected.Question.ItemId)).Grade);
    }

    [Fact]
    public async Task PartialCorrection_PreservesOtherSubjectsAndReviews_AndReturnsOnlyTarget()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = Caller(factory);
        var job = await SeedAsync(factory);
        var target = job.Questions[0];
        var other = job.Questions[1];
        var detailRoute = $"/api/mistakes/{target.ItemId}";
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(detailRoute);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var pendingResponse = await caller.GetAsync(detailRoute);
        var pending = await DataAsync<JsonElement>(pendingResponse);
        using var pendingEnvelope = JsonDocument.Parse(await pendingResponse.Content.ReadAsStringAsync());
        MistakeHttpContract.ValidateResponse("GET", detailRoute, (int)pendingResponse.StatusCode, pendingEnvelope.RootElement);
        Assert.Equal((int)ReviewStatus.PendingReview, pending.GetProperty("reviewStatus").GetInt32());
        Assert.Equal($"extract:{job.Id:N}", pending.GetProperty("sourceUploadId").GetString());
        Assert.Equal(target.Question.Id.ToString("D"), pending.GetProperty("questionId").GetString());
        AssertAnalysis(pending.GetProperty("analysisSnapshot"), "1 + 1 = ?", "3", "2",
            "Add one and one", "Actual attempt is 3 rather than 2", 1);
        var otherBefore = (await DataAsync<JsonElement>(await caller.GetAsync($"/api/mistakes/{other.ItemId}")))
            .GetProperty("analysisSnapshot").GetRawText();
        await using (var db = database.CreateContext())
        {
            var item = await db.MistakeItems.SingleAsync(i => i.Id == other.ItemId);
            item.ReviewStatus = (int)ReviewStatus.Confirmed;
            item.ReviewerId = "other-teacher";
            item.RootCause = "keep other subject review";
            await db.SaveChangesAsync();
        }
        var route = $"/api/intake/extractions/{job.Id}/questions/{target.Question.Id}";
        var originalReview = new ExtractionReviewRequest(job.Version, target.ItemId!.Value,
            (int)ReviewStatus.Confirmed, "teacher", null, null);
        using var reviewed = await caller.PostAsJsonAsync(route + "/review", originalReview);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var confirmed = await DataAsync<JsonElement>(await caller.GetAsync(detailRoute));
        Assert.Equal((int)ReviewStatus.Confirmed, confirmed.GetProperty("reviewStatus").GetInt32());
        Assert.Equal("", confirmed.GetProperty("rootCause").GetString());
        Assert.Equal(pending.GetProperty("analysisSnapshot").GetRawText(), confirmed.GetProperty("analysisSnapshot").GetRawText());
        var request = new ExtractionQuestionRevisionRequest(Guid.NewGuid(), job.Version, "Complete the target crop",
            target.Question with { StudentAnswer = "4", Explanation = "", Evidence = "The visible attempt is 4, not 2",
                Regions = [target.Question.Regions[0] with { X1 = .5 }] });
        var corrected = await DataAsync<ExtractionQuestionRevisionView>(await caller.PutAsJsonAsync(route, request));
        Assert.NotNull(corrected.Question);
        Assert.Equal(job.Version + 1, corrected.Version);
        Assert.Equal(target.ItemId, corrected.Question.ItemId);
        Assert.Equal(target.Question.Id, corrected.Question.Question.Id);
        Assert.Equal(.5, corrected.Question.Question.Regions[0].X1);
        await using (var db = database.CreateContext())
        {
            var untouched = await db.MistakeItems.SingleAsync(i => i.Id == other.ItemId);
            Assert.Equal((int)ReviewStatus.Confirmed, untouched.ReviewStatus);
            Assert.Equal("other-teacher", untouched.ReviewerId);
            Assert.Equal("keep other subject review", untouched.RootCause);
            Assert.Equal((int)ReviewStatus.PendingReview,
                (await db.MistakeItems.SingleAsync(i => i.Id == target.ItemId)).ReviewStatus);
        }
        var full = await DataAsync<ExtractionJobView>(await caller.GetAsync($"/api/intake/extractions/{job.Id}"));
        Assert.Equal(2, full.Questions.Count);
        Assert.Equal(JsonSerializer.Serialize(other), JsonSerializer.Serialize(full.Questions[1]));
        using var staleReview = await caller.PostAsJsonAsync(route + "/review", originalReview);
        Assert.Equal(HttpStatusCode.Conflict, staleReview.StatusCode);
        Assert.Contains("version_conflict", await staleReview.Content.ReadAsStringAsync());
        var correctedDetail = await DataAsync<JsonElement>(await caller.GetAsync(detailRoute));
        Assert.Equal((int)ReviewStatus.PendingReview, correctedDetail.GetProperty("reviewStatus").GetInt32());
        AssertAnalysis(correctedDetail.GetProperty("analysisSnapshot"), "1 + 1 = ?", "4", "2", "",
            "The visible attempt is 4, not 2", corrected.Version);
        Assert.Equal(otherBefore, (await DataAsync<JsonElement>(await caller.GetAsync($"/api/mistakes/{other.ItemId}")))
            .GetProperty("analysisSnapshot").GetRawText());
        using var reapproved = await caller.PostAsJsonAsync(route + "/review", originalReview with { ExpectedVersion = corrected.Version });
        Assert.Equal(HttpStatusCode.OK, reapproved.StatusCode);
        using var practice = await caller.PostAsJsonAsync(detailRoute + "/practices",
            new { studentId = job.StudentId, studentAnswer = "2", isCorrect = true, imagePaths = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, practice.StatusCode);
        Assert.Equal(correctedDetail.GetProperty("analysisSnapshot").GetRawText(),
            (await DataAsync<JsonElement>(await caller.GetAsync(detailRoute))).GetProperty("analysisSnapshot").GetRawText());
        var byUpload = await DataAsync<JsonElement>(await caller.GetAsync($"/api/mistakes/by-upload/extract:{job.Id:N}"));
        Assert.Equal(2, byUpload.GetProperty("items").GetArrayLength());
        Assert.All(byUpload.GetProperty("items").EnumerateArray(), item => Assert.False(item.TryGetProperty("analysisSnapshot", out _)));

        // A correction is not a request for student supplements and does not confirm review.
        var reclassified = request with { RequestKey = Guid.NewGuid(), ExpectedVersion = corrected.Version,
            Question = corrected.Question.Question with { Outcome = "correct", StudentAnswer = "2", Evidence = "The visible answer is 2" } };
        var changed = await DataAsync<ExtractionQuestionRevisionView>(await caller.PutAsJsonAsync(route, reclassified));
        Assert.NotNull(changed.Question);
        Assert.Equal(target.ItemId, changed.Question.ItemId);
        await using (var db = database.CreateContext())
            Assert.Equal((int)ReviewStatus.Rejected, (await db.MistakeItems.SingleAsync(i => i.Id == target.ItemId)).ReviewStatus);
        Assert.False((await DataAsync<JsonElement>(await caller.GetAsync(detailRoute))).TryGetProperty("analysisSnapshot", out _));

        using var replayResponse = await caller.PutAsJsonAsync(route, request);
        var replayText = await replayResponse.Content.ReadAsStringAsync();
        var replay = await DataAsync<ExtractionQuestionRevisionView>(replayResponse);
        Assert.Equal(corrected.Version, replay.Version);
        Assert.True(replay.Superseded);
        Assert.Null(replay.Question);
        Assert.DoesNotContain(other.Question.Statement, replayText);
        Assert.DoesNotContain(other.Question.Id.ToString(), replayText);
        Assert.DoesNotContain("questions", JsonDocument.Parse(replayText).RootElement.GetProperty("data").ToString());
        Assert.Equal(changed.Version, (await DataAsync<ExtractionJobView>(await caller.GetAsync($"/api/intake/extractions/{job.Id}"))).Version);
        Assert.Equal(HttpStatusCode.Conflict, (await caller.PutAsJsonAsync(route, request with { Reason = "different" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await caller.PutAsJsonAsync(route, request with { RequestKey = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await caller.PutAsJsonAsync($"/api/intake/extractions/{job.Id}/questions",
            new ExtractionRevisionRequest(request.RequestKey, request.ExpectedVersion, request.Reason, [request.Question]))).StatusCode);
    }

    [Fact]
    public async Task PartialCorrection_EnforcesIdentityOwnerValidationAndAdminCsrf()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = Caller(factory);
        var job = await SeedAsync(factory);
        var question = job.Questions[0].Question;
        var route = $"/api/intake/extractions/{job.Id}/questions/{question.Id}";
        var request = new ExtractionQuestionRevisionRequest(Guid.NewGuid(), job.Version, "Adjust target", question);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(route, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await caller.PutAsJsonAsync(route, request with { ExpectedVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await caller.PutAsJsonAsync(route,
            request with { Question = question with { Id = Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await caller.PutAsJsonAsync(route,
            request with { Question = question with { Regions = [new(Guid.NewGuid(), 0, 0, 1, 1)] } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await caller.PutAsJsonAsync(route,
            request with { Question = question with { Evidence = "" } })).StatusCode);
        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await caller.PutAsJsonAsync(
            $"/api/intake/extractions/{job.Id}/questions/{unknown}", request with { Question = question with { Id = unknown } })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExtractionService>();
        var denied = await Assert.ThrowsAsync<ExtractionException>(() => service.ReviseQuestionAsync(
            new("another-caller", "another-caller"), job.Id, question.Id, request));
        Assert.Equal(404, denied.Status);
        using var admin = factory.CreateClient();
        var session = await factory.SeedSessionAsync(admin);
        var adminRoute = $"/admin/api/extractions/{job.Id}/questions/{question.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync(adminRoute, request)).StatusCode);
        await factory.AddCsrfAsync(admin, session);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(adminRoute, request)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentPartialCorrections_CommitOneVersionAndRetainAllQuestions()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = Caller(factory);
        var job = await SeedAsync(factory);
        var replies = await Task.WhenAll(job.Questions.Select(q => caller.PutAsJsonAsync(
            $"/api/intake/extractions/{job.Id}/questions/{q.Question.Id}",
            new ExtractionQuestionRevisionRequest(Guid.NewGuid(), job.Version, "Concurrent correction",
                q.Question with { Regions = [q.Question.Regions[0] with { X1 = .5 }] }))));
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var reply in replies) reply.Dispose();
        var read = await DataAsync<ExtractionJobView>(await caller.GetAsync($"/api/intake/extractions/{job.Id}"));
        Assert.Equal(job.Version + 1, read.Version);
        Assert.Equal(job.Questions.Select(q => (q.Question.Id, q.ItemId)), read.Questions.Select(q => (q.Question.Id, q.ItemId)));
    }

    [Fact]
    public async Task SuggestionsAndSupplementRoutesEnforceAuthCsrfVersionAndOriginalIdentity()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var factory = new ExtractionHostFixture(database.Connection);
        using var caller = Caller(factory);
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var admin = factory.CreateClient(new() { AllowAutoRedirect = false });
        var session = await factory.SeedSessionAsync(admin);
        var job = await SeedAsync(factory);
        var target = job.Questions[0];
        var detail = $"/api/mistakes/{target.ItemId}";
        var originalAnalysis = (await DataAsync<JsonElement>(await caller.GetAsync(detail))).GetProperty("analysisSnapshot").GetRawText();
        var root = $"/api/intake/extractions/{job.Id}";
        var request = new ExtractionProposalRequest(Guid.NewGuid(), job.Version, "Check the missing margin", "questions", [target.Question.Id], [], []);
        using var denied = await anonymous.PostAsJsonAsync(root + "/proposals", request);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var csrf = await admin.PostAsJsonAsync($"/admin/api/extractions/{job.Id}/proposals", request);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        var candidate = await DataAsync<ExtractionProposalView>(await caller.PostAsJsonAsync(root + "/proposals", request));
        Assert.Equal("Pending", candidate.Status);
        Assert.Equal(originalAnalysis, (await DataAsync<JsonElement>(await caller.GetAsync(detail))).GetProperty("analysisSnapshot").GetRawText());
        var candidates = await DataAsync<List<ExtractionProposalView>>(await caller.GetAsync(root + "/proposals"));
        Assert.Contains(candidates, p => p.Id == candidate.Id);
        var supplement = await DataAsync<ExtractionSupplementView>(await caller.PostAsJsonAsync(
            root + $"/questions/{target.Question.Id}/supplements", new ExtractionSupplementRequest(Guid.NewGuid(), job.Version, target.ItemId!.Value, "Please add the second page")));
        Assert.Equal(target.ItemId, supplement.ItemId);
        Assert.Equal(target.Question.Id, supplement.QuestionId);
        Assert.Equal(job.Version + 1, supplement.Version);
        using var stale = await caller.PostAsJsonAsync(root + $"/proposals/{candidate.Id}/adopt", new ExtractionProposalAdoptionRequest(Guid.NewGuid(), job.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var data = new MultipartFormDataContent();
        data.Add(new StringContent(Guid.NewGuid().ToString()), "requestKey");
        data.Add(new StringContent(supplement.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)), "expectedVersion");
        data.Add(new StringContent(target.ItemId.Value.ToString()), "itemId");
        var image = new ByteArrayContent(ExtractionHttpPostgresTests.TestPng());
        image.Headers.ContentType = new("image/png"); data.Add(image, "images", "page.png");
        var accepted = await DataAsync<ExtractionSupplementView>(await caller.PostAsync(root + $"/questions/{target.Question.Id}/supplements/{supplement.Id}/images", data));
        Assert.Equal("Completed", accepted.Status);
        var current = await DataAsync<ExtractionJobView>(await caller.GetAsync(root));
        Assert.Equal(job.Id, current.Id);
        Assert.Equal(target.ItemId, current.Questions[0].ItemId);
        Assert.Null(current.Questions[0].Supplement);
        Assert.Equal(JsonSerializer.Serialize(job.Questions[1].Question), JsonSerializer.Serialize(current.Questions[1].Question));
        Assert.Equal(originalAnalysis, (await DataAsync<JsonElement>(await caller.GetAsync(detail))).GetProperty("analysisSnapshot").GetRawText());
        var retired = current.Questions[0].Images[0].Path;
        var corrected = await DataAsync<ExtractionJobView>(await caller.PutAsJsonAsync(root + "/questions",
            new ExtractionRevisionRequest(Guid.NewGuid(), current.Version, "Replace both shared references before checking retirement",
                current.Questions.Select(q => q.Question with { Regions = [q.Question.Regions[0] with { X1 = .5 }] }).ToList())));
        using var reviewed = await caller.PostAsJsonAsync(root + $"/questions/{target.Question.Id}/review",
            new ExtractionReviewRequest(corrected.Version, target.ItemId.Value, (int)ReviewStatus.Confirmed, "teacher", null, null));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        using var invalidReference = await caller.PostAsJsonAsync($"/api/mistakes/{target.ItemId}/practices",
            new { studentId = job.StudentId, studentAnswer = "2", isCorrect = true, imagePaths = new[] { retired } });
        Assert.Equal(HttpStatusCode.Conflict, invalidReference.StatusCode);
        Assert.Contains("extraction_object_retired", await invalidReference.Content.ReadAsStringAsync());
    }

    private static void AssertAnalysis(JsonElement snapshot, string statement, string attempt, string answer,
        string explanation, string evidence, int version)
    {
        Assert.Equal(statement, snapshot.GetProperty("statement").GetString());
        Assert.Equal(attempt, snapshot.GetProperty("studentAnswer").GetString());
        Assert.Equal(answer, snapshot.GetProperty("referenceAnswer").GetString());
        Assert.Equal(explanation, snapshot.GetProperty("explanation").GetString());
        Assert.Equal(evidence, snapshot.GetProperty("evidence").GetString());
        Assert.Equal(version, snapshot.GetProperty("sourceVersion").GetInt32());
    }

    private static HttpClient Caller(ExtractionHostFixture factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Emenda-Api-Key", ExtractionHostFixture.CallerKey);
        return client;
    }

    private static async Task<ExtractionJobView> SeedAsync(ExtractionHostFixture factory)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExtractionService>();
        var access = new ExtractionAccess("http-tests", "http-tests");
        var queued = await service.SubmitAsync(access, new("student-partial", 4, "mixed-subjects",
            [new() { FileName = "page.png", ContentType = "image/png", Content = ExtractionHttpPostgresTests.TestPng() }]));
        await service.RunQueuedAsync(default);
        var job = await service.GetAsync(access, queued.Id);
        return await service.ReviseAsync(access, job.Id, new(Guid.NewGuid(), job.Version, "Add another subject",
            [job.Questions[0].Question, job.Questions[0].Question with { Id = Guid.NewGuid(), Subject = 3, Statement = "Private other-subject question" }]));
    }

    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
