using System.Text.Json;
using JobChatBot.Api;
using Microsoft.Playwright;
internal static class NaukriProfileChecks
{
    public static async Task<int> Run(IPage page)
    {
        var count=0; var name="Old Name"; var filename=""; var uploads=0; var nameSaves=0;
        void Check(bool value,string message) { if (!value) throw new Exception(message); count++; }
        var resume=Path.Combine(Path.GetTempPath(),"naukri-profile-"+Guid.NewGuid().ToString("N")+".pdf");
        await File.WriteAllTextAsync(resume,"%PDF-offline-upload-fixture");
        await page.UnrouteAllAsync();
        await page.RouteAsync("**/*",async route => {
            var uri=new Uri(route.Request.Url);
            if (uri.AbsolutePath=="/fixture/name")
            {
                name=JsonDocument.Parse(route.Request.PostData!).RootElement.GetProperty("name").GetString()!; nameSaves++;
                await route.FulfillAsync(new() { Status=200,Body="ok" }); return;
            }
            if (uri.AbsolutePath=="/fixture/upload")
            {
                filename=route.Request.PostData!; uploads++;
                await route.FulfillAsync(new() { Status=200,Body="ok" }); return;
            }
            var html=$$"""
            <span data-testid="profile-name">{{name}}</span>
            <button data-testid="edit-basic-details" onclick="document.querySelector('#editor').style.display='block'">Edit</button>
            <div id="editor" role="dialog" style="display:none"><input id="name" value="{{name}}"><button onclick="fetch('/fixture/name',{method:'POST',body:JSON.stringify({name:document.querySelector('#name').value})}).then(()=>{document.querySelector('[data-testid=profile-name]').textContent=document.querySelector('#name').value;document.querySelector('#editor').style.display='none'})">Save</button></div>
            <div data-testid="resume-section">{{filename}}</div>
            <input data-testid="resume-upload" type="file" onchange="fetch('/fixture/upload',{method:'POST',body:this.files[0].name}).then(()=>{document.querySelector('[role=status]').textContent='Resume has been successfully uploaded'})">
            <div role="status"></div>
            """;
            await route.FulfillAsync(new() { Status=200,ContentType="text/html",Body=html });
        });
        try
        {
            await page.GotoAsync("https://www.naukri.com/mnjuser/profile");
            var result=await NaukriProfileUpdate.Update(page,"Correct Name",resume);
            Check(result.Verified && uploads==1 && nameSaves==1 && name=="Correct Name", "Profile update saves the exact configured name and uploads the chosen resume once");
            Check(filename==Path.GetFileName(resume),"Uploaded resume filename matches the selected local file");
            result=await NaukriProfileUpdate.Update(page,"Correct Name",resume);
            Check(result.Verified && uploads==2 && nameSaves==1,"Repeated cycle reuploads resume and verifies unchanged name without artificial edits");
            await page.GotoAsync("https://www.naukri.com.evil.example/mnjuser/profile");
            Check(!(await NaukriProfileUpdate.Update(page,"Other Name",resume)).Verified && uploads==2,"Profile updater rejects lookalike origins before writing");
            await page.GotoAsync("https://www.naukri.com/mnjuser/profile");
            await page.SetContentAsync("<span data-testid='profile-name'>Correct Name</span><div>Unsupported resume layout</div>");
            Check(!(await NaukriProfileUpdate.Update(page,"Correct Name",resume)).Verified && uploads==2,"Unknown upload layout pauses without claiming profile success");
        }
        finally { File.Delete(resume); }
        return count;
    }
}
