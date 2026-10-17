using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Services;
namespace Themearr.API.Tests;
public class AudioRepairEndpointTests
{
    [Theory][InlineData("preview")][InlineData("apply")][InlineData("status")][InlineData("cancel")]
    public void All_repair_routes_stay_inside_auth_boundary(string route) => Assert.True(ApiAuthMiddleware.RequiresAuth(new PathString("/api/audio-repair/"+route)));
    [Fact]
    public async Task Invalid_scope_and_unknown_preview_are_rejected_and_report_hides_paths()
    {
        using var f=new RepairTestFixture();await f.Repair.RecoverAsync();var c=new AudioRepairController(f.Repair);
        Assert.IsType<BadRequestObjectResult>(await c.Preview(new("bad",f.Options),default));
        Assert.IsType<ConflictObjectResult>(c.Apply(new(Guid.NewGuid(),["unknown"])));
        var p=await f.Preview();Assert.IsType<ConflictObjectResult>(c.Apply(new(p.PreviewId,["../../elsewhere"])));
        var started=Assert.IsType<AcceptedResult>(c.Apply(new(p.PreviewId,[p.Items[0].Id])));var run=Assert.IsType<AudioRepairStartResult>(started.Value);await f.Finish(run.RunId);
        var json=JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(c.Status()).Value);Assert.DoesNotContain(f.Dir.Path,json);Assert.DoesNotContain("SourcePath",json);Assert.DoesNotContain("source_ref",json);
    }
}
