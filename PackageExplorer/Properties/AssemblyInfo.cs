using System.Reflection;
using System.Windows;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
#if NUSPEC_EDITOR
[assembly: AssemblyTitle("NuSpec Editor")]
#else
[assembly: AssemblyTitle("NuGet Package Explorer")]
#endif

#if NUSPEC_EDITOR
[assembly:
    AssemblyDescription(
        "This is a NuSpec editor tool which lets you create and edit the metadata of a .nuspec xml file. After installing it, you can double click on .nuspec files to open them."
        )]
#else
[assembly:
    AssemblyDescription(
        "This is a NuGet package explorer tool which lets you view the metadata of a .nupkg package. After installing it, you can double click on .nupkg packages to open them in Package Explorer."
        )]
#endif

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None, //where theme specific resource dictionaries are located
    //(used if a resource is not found in the page, 
    // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly //where the generic resource dictionary is located
    //(used if a resource is not found in the page, 
    // app, or any theme specific resource dictionaries)
    )]