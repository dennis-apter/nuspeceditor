using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;

[assembly: AssemblyCompany("")]

#if NUSPEC_EDITOR
[assembly: AssemblyProduct("NuSpec Editor")]
#else
[assembly: AssemblyProduct("NuGet Package Explorer")]
#endif

[assembly: AssemblyCopyright("\x00a9 Luan Nguyen. All rights reserved.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: NeutralResourcesLanguage("en-US")]
[assembly: AssemblyVersion("3.8.0.1")]
[assembly: AssemblyFileVersion("3.8.0.1")]