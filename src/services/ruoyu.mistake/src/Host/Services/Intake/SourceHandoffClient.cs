using System.Net;
using System.Net.Http.Json;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
namespace Ruoyu.Study.Mistake.Host.Services.Intake;

public sealed class SourceHandoffClient(HttpClient http,StudentServiceAuthenticationOptions identity)
{
    private static string Route(Guid source,Guid revision)=>$"/api/integrations/mistake/uploads/{source:D}/snapshots/{revision:D}/handoff";
    public async Task<SourceHandoffReceipt?> ReadAsync(Guid source,Guid revision,CancellationToken ct)
    {
        if(!identity.Enabled)throw new SourceIntakeException("handoff_provider_unavailable",503);
        try
        {
            using var response=await http.GetAsync(Route(source,revision),ct);
            if(response.StatusCode==HttpStatusCode.NotFound)return null;
            if(!response.IsSuccessStatusCode)throw new SourceIntakeException("handoff_receipt_unavailable",503);
            return await response.Content.ReadFromJsonAsync<SourceHandoffReceipt>(IntakeCanonical.Json,ct) ?? throw new SourceIntakeException("handoff_receipt_unavailable",503);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(SourceIntakeException){throw;}
        catch(Exception){throw new SourceIntakeException("handoff_receipt_unavailable",503);}
    }
    public async Task PutAsync(Guid source,Guid revision,SourceHandoffRequest request,CancellationToken ct)
    {
        if(!identity.Enabled)throw new SourceIntakeException("handoff_provider_unavailable",503);
        try
        {
            using var response=await http.PutAsJsonAsync(Route(source,revision),request,IntakeCanonical.Json,ct);
            if(response.StatusCode==HttpStatusCode.Conflict)throw new SourceIntakeException("handoff_payload_conflict",409);
            // Only the authenticated historical GET proves the committed logical result.
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(SourceIntakeException){throw;}
        catch(Exception){throw new SourceIntakeException("handoff_put_unknown",503);}
    }
}
