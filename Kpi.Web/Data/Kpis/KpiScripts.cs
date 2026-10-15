namespace Kpi.Web.Data.Kpis;

/// <summary>
/// The URL a KPI screen imports a script module by, versioned with the file's write time ("?v=ticks") as
/// KpiMobile versions kpi.css. Static files are served with no cache headers, so a browser may reuse an old copy
/// of a script for days by its own rule of thumb (it did: a new Sales Performance page ran the previous timelapse
/// script) - a changed file gets a new URL instead. Every KPI script import goes through this, and every import of
/// one module must, since a module is loaded once per URL. Singleton.
/// </summary>
public sealed class KpiScripts(IWebHostEnvironment env)
{
    /// <param name="path">the module's path as imported, e.g. "./Components/Shared/Kpis/KpiPanel.razor.js"</param>
    public string Url(string path)
    {
        // the web root's provider covers wwwroot and, in development, the collocated .razor.js files beside the source
        var file = env.WebRootFileProvider.GetFileInfo(path.TrimStart('.', '/'));
        return file.Exists ? $"{path}?v={file.LastModified.UtcTicks}" : path;
    }
}
