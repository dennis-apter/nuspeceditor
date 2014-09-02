namespace PackageExplorerViewModel
{
    public static class Constants
    {
#if NUSPEC_EDITOR
        public const string UserAgentClient = "NuSpec Editor";
#else
        public const string UserAgentClient = "NuGet Package Explorer";
#endif
        internal const string ContentForInit = "param($installPath, $toolsPath, $package)";
        internal const string ContentForInstall = "param($installPath, $toolsPath, $package, $project)";

        internal const string ContentForBuildFile = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">

</Project>";
    }
}