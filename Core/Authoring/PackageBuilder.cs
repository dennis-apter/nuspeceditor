﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using NuGet.Resources;

namespace NuGet
{
    public sealed class PackageBuilder : IPackageBuilder
    {
        private const string DefaultContentType = "application/octet";
        internal const string ManifestRelationType = "manifest";

        private readonly List<PackageFileBase> _sourceFiles;

        public PackageBuilder(string path)
            : this(path, Path.GetDirectoryName(path))
        {
        }

        public PackageBuilder(string path, string basePath)
            : this()
        {
            using (Stream stream = File.OpenRead(path))
            {
                // Deserialize the document and extract the metadata
                var manifest = Manifest.ReadFrom(stream);
                ReadManifest(manifest, basePath);
            }
        }

        public PackageBuilder(Manifest manifest, string basePath)
            : this()
        {
            ReadManifest(manifest, basePath);
        }

        public PackageBuilder()
        {
            Files = new Collection<IPackageFile>();
            DependencySets = new Collection<PackageDependencySet>();
            FrameworkReferences = new Collection<FrameworkAssemblyReference>();
            PackageAssemblyReferences = new Collection<PackageReferenceSet>();
            Authors = new HashSet<string>();
            Owners = new HashSet<string>();
            Tags = new HashSet<string>();
            _sourceFiles = new List<PackageFileBase>();
        }

        public ISet<string> Authors { get; private set; }
        public ISet<string> Owners { get; private set; }

        public ISet<string> Tags { get; private set; }

        public Collection<PackageReferenceSet> PackageAssemblyReferences { get; private set; }

        public Collection<FrameworkAssemblyReference> FrameworkReferences { get; private set; }

        #region IPackageBuilder Members

        public string Id { get; set; }

        public SemanticVersion Version { get; set; }

        public string Title { get; set; }

        public Uri IconUrl { get; set; }

        public Uri LicenseUrl { get; set; }

        public Uri ProjectUrl { get; set; }

        public bool RequireLicenseAcceptance { get; set; }

        public bool DevelopmentDependency { get; set; }

        public string Description { get; set; }

        public string Summary { get; set; }

        public string ReleaseNotes { get; set; }

        public string Copyright { get; set; }

        public string Language { get; set; }

        public Collection<PackageDependencySet> DependencySets
        {
            get;
            private set;
        }

        public Collection<IPackageFile> Files
        {
            get;
            private set;
        }

        public Version MinClientVersion
        {
            get;
            set;
        }

        public IDictionary<string, string> TemplateValues
        {
            get;
            private set;
        }

        IEnumerable<string> IPackageMetadata.Authors
        {
            get { return Authors; }
        }

        IEnumerable<string> IPackageMetadata.Owners
        {
            get { return Owners; }
        }

        string IPackageMetadata.Tags
        {
            get { return String.Join(" ", Tags); }
        }

        IEnumerable<PackageReferenceSet> IPackageMetadata.PackageAssemblyReferences
        {
            get { return PackageAssemblyReferences; }
        }

        IEnumerable<PackageDependencySet> IPackageMetadata.DependencySets
        {
            get
            {
                return DependencySets;
            }
        }

        IEnumerable<FrameworkAssemblyReference> IPackageMetadata.FrameworkAssemblies
        {
            get { return FrameworkReferences; }
        }

        public bool HasPendingSourceFilesAutoPopulating
        {
            get { return _sourceFiles.Count > 0; }
        }

        public void Save(Stream stream)
        {
            // Make sure we're saving a valid package id
            PackageIdValidator.ValidatePackageId(Id);

            // Throw if the package doesn't contain any dependencies nor content
            if (!Files.Any() && !DependencySets.Any() && !FrameworkReferences.Any())
            {
                throw new InvalidOperationException(NuGetResources.CannotCreateEmptyPackage);
            }

            ValidateDependencySets(Version, DependencySets);
            ValidateReferenceAssemblies(Files, PackageAssemblyReferences);

            using (Package package = Package.Open(stream, FileMode.Create))
            {
                // Validate and write the manifest
                WriteManifest(package, DetermineMinimumSchemaVersion(Files));

                // Write the files to the package
                WriteFiles(package);

                // Copy the metadata properties back to the package
                package.PackageProperties.Creator = String.Join(",", Authors);
                package.PackageProperties.Description = Description;
                package.PackageProperties.Identifier = Id;
                package.PackageProperties.Version = Version.ToString();
                package.PackageProperties.Language = Language;
                package.PackageProperties.Keywords = ((IPackageMetadata)this).Tags;
                package.PackageProperties.Title = Title;
#if NUSPEC_EDITOR
                package.PackageProperties.Subject = "NuGet Package Explorer";
#else
                package.PackageProperties.Subject = "NuGet Package Explorer";
#endif
            }
        }

        private static int DetermineMinimumSchemaVersion(Collection<IPackageFile> Files)
        {
            if (HasXdtTransformFile(Files))
            {
                // version 5
                return ManifestVersionUtility.XdtTransformationVersion;
            }

            if (RequiresV4TargetFrameworkSchema(Files))
            {
                // version 4
                return ManifestVersionUtility.TargetFrameworkSupportForDependencyContentsAndToolsVersion;
            }

            return ManifestVersionUtility.DefaultVersion;
        }

        private static bool HasXdtTransformFile(ICollection<IPackageFile> contentFiles)
        {
            return contentFiles.Any(file =>
                file.Path != null &&
                file.Path.StartsWith(Constants.ContentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                (file.Path.EndsWith(".install.xdt", StringComparison.OrdinalIgnoreCase) ||
                 file.Path.EndsWith(".uninstall.xdt", StringComparison.OrdinalIgnoreCase)));
        }

        private static bool RequiresV4TargetFrameworkSchema(ICollection<IPackageFile> files)
        {
            // check if any file under Content or Tools has TargetFramework defined
            bool hasContentOrTool = files.Any(
                f => f.TargetFramework != null &&
                     (f.Path.StartsWith(Constants.ContentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                      f.Path.StartsWith(Constants.ToolsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

            if (hasContentOrTool)
            {
                return true;
            }

            // now check if the Lib folder has any empty framework folder
            bool hasEmptyLibFolder = files.Any(
                f => f.TargetFramework != null &&
                     f.Path.StartsWith(Constants.LibDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                     f.EffectivePath == Constants.PackageEmptyFileName);

            return hasEmptyLibFolder;
        }

        #endregion

        internal static void ValidateDependencySets(SemanticVersion version, IEnumerable<PackageDependencySet> dependencies)
        {
            if (version == null)
            {
                // We have independent validation for null-versions.
                return;
            }

            if (String.IsNullOrEmpty(version.SpecialVersion))
            {
                // If we are creating a production package, do not allow any of the dependencies to be a prerelease version.
                var prereleaseDependency = dependencies.SelectMany(set => set.Dependencies).FirstOrDefault(IsPrereleaseDependency);
                if (prereleaseDependency != null)
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.Manifest_InvalidPrereleaseDependency, prereleaseDependency.ToString()));
                }
            }
        }

        internal static void ValidateReferenceAssemblies(IEnumerable<IPackageFile> files, IEnumerable<PackageReferenceSet> packageAssemblyReferences)
        {
            var libFiles = new HashSet<string>(from file in files
                                               where !String.IsNullOrEmpty(file.Path) && file.Path.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
                                               select Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var reference in packageAssemblyReferences.SelectMany(p => p.References))
            {
                if (!libFiles.Contains(reference) &&
                    !libFiles.Contains(reference + ".dll") &&
                    !libFiles.Contains(reference + ".exe") &&
                    !libFiles.Contains(reference + ".winmd"))
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.Manifest_InvalidReference, reference));
                }
            }
        }

        private static bool IsPrereleaseDependency(PackageDependency dependency)
        {
            IVersionSpec versionSpec = dependency.VersionSpec;
            if (versionSpec != null)
            {
                return (versionSpec.MinVersion != null && !String.IsNullOrEmpty(dependency.VersionSpec.MinVersion.SpecialVersion)) ||
                       (versionSpec.MaxVersion != null && !String.IsNullOrEmpty(dependency.VersionSpec.MaxVersion.SpecialVersion));
            }
            return false;
        }

        private void ReadManifest(Manifest manifest, string basePath)
        {
            bool filesAdded = false;
            if (manifest.IsTemplate)
            {
                AddFiles(manifest, basePath);
                filesAdded = true;

                var values = FillTemplateValues(manifest.Metadata);
                FillTemplateDependencyVersions(manifest.Metadata, values);
                TemplateValues = new ReadOnlyDictionary<string, string>(values);
            }

            IPackageMetadata metadata = manifest.Metadata;

            Id = metadata.Id;
            Version = metadata.Version;
            Title = metadata.Title;
            Authors.AddRange(metadata.Authors);
            Owners.AddRange(metadata.Owners);
            IconUrl = metadata.IconUrl;
            LicenseUrl = metadata.LicenseUrl;
            ProjectUrl = metadata.ProjectUrl;
            RequireLicenseAcceptance = metadata.RequireLicenseAcceptance;
            Description = metadata.Description;
            Summary = metadata.Summary;
            ReleaseNotes = metadata.ReleaseNotes;
            Language = metadata.Language;
            Copyright = metadata.Copyright;
            MinClientVersion = metadata.MinClientVersion;
            DevelopmentDependency = metadata.DevelopmentDependency;

            if (metadata.Tags != null)
            {
                Tags.AddRange(ParseTags(metadata.Tags));
            }

            DependencySets.AddRange(metadata.DependencySets);
            FrameworkReferences.AddRange(metadata.FrameworkAssemblies);
            if (metadata.PackageAssemblyReferences != null)
            {
                PackageAssemblyReferences.AddRange(metadata.PackageAssemblyReferences);
            }

            if (!filesAdded)
            {
                AddFiles(manifest, basePath);
            }
        }

        private void FillTemplateDependencyVersions(ManifestMetadata metadata, Dictionary<string, string> values)
        {
            foreach (var dependencySet in metadata.DependencySets)
            {
                foreach (var dependency in dependencySet.Dependencies)
                {
                    if (!string.IsNullOrEmpty(dependency.Version))
                        continue;

                    var basePath = ResolveCodeBaseDirectory(metadata, dependencySet.TargetFramework);
                    if (null == basePath)
                    {
                        continue;
                    }

                    string dependencyFileName = Path.Combine(basePath, dependency.Id + ".dll");
                    if (!File.Exists(dependencyFileName))
                    {
                        dependencyFileName = null;
                        foreach (var candidateFile in Directory.EnumerateFiles(basePath, dependency.Id + "*.dll"))
                        {
                            var candidateAssembly = AssemblyDefinition.ReadAssembly(candidateFile);
                            var candidateTitle = ResolveAssemblyTitle(candidateAssembly);
                            if (candidateTitle == dependency.Id)
                            {
                                dependencyFileName = candidateFile;
                                break;
                            }
                        }

                        if (dependencyFileName == null)
                        {
                            do
                            {
                                basePath = Path.GetDirectoryName(basePath);
                                if (basePath == null)
                                {
                                    break;
                                }

                                dependencyFileName = Path.Combine(basePath, dependency.Id + ".dll");
                            }
                            while (!File.Exists(dependencyFileName));

                            if (basePath == null)
                            {
                                if (!string.IsNullOrEmpty(dependencySet.TargetFramework))
                                {
                                    dependencyFileName += " (" + dependencySet.TargetFramework + ")";
                                }

                                throw new FileNotFoundException(string.Format(
                                    CultureInfo.CurrentCulture,
                                    NuGetResources.PackageAuthoring_FileNotFound,
                                    dependencyFileName));
                            }
                        }
                    }

                    var assembly = AssemblyDefinition.ReadAssembly(dependencyFileName);
                    dependency.Version = ResolveAssemblyVersion(assembly);
                    if (dependency.Version == null)
                        throw new ArgumentException(string.Format(
                            "Can't resolve a version of dependency {0}.", dependency.Id));

                    var key = CreateTemplateDependencyKey(dependencySet.TargetFramework, dependency.Id);
                    values.Add(key, dependency.Version);
                }
            }
        }

        internal static string CreateTemplateDependencyKey(string targetFramework, string id)
        {
            if (targetFramework != null)
            {
                targetFramework = VersionUtility.GetShortFrameworkName(targetFramework);
            }

            return "dependency:" + targetFramework + ":" + id;
        }

        private string ResolveCodeBaseDirectory(ManifestMetadata metadata, string targetFramework)
        {
            IPackageFile primaryAssemblyFile;

            // Search in libs
            if (targetFramework != null)
            {
                var sn = VersionUtility.GetShortFrameworkName(targetFramework);
                var path = string.Format(@"lib\{0}\{1}.dll", sn, metadata.Id);
                primaryAssemblyFile = Files.FirstOrDefault(f =>
                    f.Path.Equals(path, StringComparison.InvariantCultureIgnoreCase));
            }
            else
            {
                var path = "\\" + metadata.Id + ".dll";
                primaryAssemblyFile = Files.FirstOrDefault(f =>
                    f.Path.StartsWith("lib", StringComparison.InvariantCultureIgnoreCase) &&
                    f.Path.EndsWith(path, StringComparison.InvariantCultureIgnoreCase));
            }
            
            // Search in libs for build
            if (primaryAssemblyFile == null)
            {
                if (targetFramework != null)
                {
                    var sn = VersionUtility.GetShortFrameworkName(targetFramework);
                    var path = string.Format(@"build\{0}\lib\{1}.dll", sn, metadata.Id);
                    primaryAssemblyFile = Files.FirstOrDefault(f =>
                        f.Path.Equals(path, StringComparison.InvariantCultureIgnoreCase));
                }
                else
                {
                    var path = "\\lib\\" + metadata.Id + ".dll";
                    primaryAssemblyFile = Files.FirstOrDefault(f =>
                        f.Path.StartsWith("build", StringComparison.InvariantCultureIgnoreCase) &&
                        f.Path.EndsWith(path, StringComparison.InvariantCultureIgnoreCase));
                }
            }
            
            // Search in libs as content
            if (primaryAssemblyFile == null)
            {
                if (targetFramework != null)
                {
                    var sn = VersionUtility.GetShortFrameworkName(targetFramework);
                    var path = string.Format(@"content\{0}\lib\{1}.dll", sn, metadata.Id);
                    primaryAssemblyFile = Files.FirstOrDefault(f =>
                        f.Path.Equals(path, StringComparison.InvariantCultureIgnoreCase));
                }
                else
                {
                    var path = "\\lib\\" + metadata.Id + ".dll";
                    primaryAssemblyFile = Files.FirstOrDefault(f =>
                        f.Path.StartsWith("content", StringComparison.InvariantCultureIgnoreCase) &&
                        f.Path.EndsWith(path, StringComparison.InvariantCultureIgnoreCase));
                }
            }
            
            if (primaryAssemblyFile != null)
                return Path.GetDirectoryName(primaryAssemblyFile.OriginalPath);

            return null;
        }

        private Dictionary<string, string> FillTemplateValues(ManifestMetadata metadata)
        {
            string primaryAssemblyPath = null;
            var values = new Dictionary<string, string>();
            if (Placeholders.Id.Equals(metadata.Id, StringComparison.InvariantCultureIgnoreCase))
            {
                values.Add(Placeholders.Id, null);
            }
            else
            {
                primaryAssemblyPath = "\\" + metadata.Id + ".dll";
            }

            if (Placeholders.Version.Equals(metadata.Version, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Version, null);

            if (Placeholders.Title.Equals(metadata.Title, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Title, null);

            if (Placeholders.Author.Equals(metadata.Authors, StringComparison.InvariantCultureIgnoreCase) ||
                Placeholders.Author.Equals(metadata.Owners, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Author, null);

            if (Placeholders.Copyright.Equals(metadata.Copyright, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Copyright, null);

            if (Placeholders.Description.Equals(metadata.Description, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Description, null);

            bool resolved = false;
            foreach (var file in Files)
            {
                if (!file.Path.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase) ||
                    file.Path.EndsWith(".resources.dll", StringComparison.InvariantCultureIgnoreCase))
                    continue;

                if (!file.Path.StartsWith("lib", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (!file.Path.StartsWith("build", StringComparison.InvariantCultureIgnoreCase) &&
                        !file.Path.StartsWith("content", StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    var dir = Path.GetDirectoryName(file.Path);
                    if (!dir.EndsWith("lib", StringComparison.InvariantCultureIgnoreCase))
                        continue;
                }

                var assembly = AssemblyDefinition.ReadAssembly(
                    file.OriginalPath, 
                    new ReaderParameters { ReadSymbols = true });

                _sourceFiles.AddRange(GetSourceFiles(assembly));

                if (primaryAssemblyPath != null &&
                    !file.Path.EndsWith(primaryAssemblyPath, StringComparison.InvariantCultureIgnoreCase))
                    continue;

                TryResolveAssemblyId(values, file);
                TryResolveAssemblyVersion(values, assembly, file);
                TryResolveAssemblyTitle(values, assembly, file);
                TryResolveAssemblyAuthor(values, assembly, file);
                TryResolveAssemblyCopyright(values, assembly, file);
                TryResolveAssemblyDescription(values, assembly, file);

                resolved = true;
            }

            var unresolvedPlaceholders = values.Where(p => p.Value == null).Select(p => p.Key).ToList();
            if (!resolved || unresolvedPlaceholders.Count > 0)
                throw new ArgumentException(string.Format("Can't resolve template values ({0})", string.Join(", ", unresolvedPlaceholders)));

            foreach (var placeholder in values)
            {
                switch (placeholder.Key)
                {
                    case Placeholders.Id:
                        metadata.Id = placeholder.Value;
                        break;
                    case Placeholders.Version:
                        metadata.Version = placeholder.Value;
                        break;
                    case Placeholders.Title:
                        metadata.Title = placeholder.Value;
                        break;
                    case Placeholders.Author:
                        if (Placeholders.Author.Equals(metadata.Authors, StringComparison.InvariantCultureIgnoreCase))
                            metadata.Authors = placeholder.Value;
                        if (Placeholders.Author.Equals(metadata.Owners, StringComparison.InvariantCultureIgnoreCase))
                            metadata.Owners = placeholder.Value;
                        break;
                    case Placeholders.Copyright:
                        metadata.Copyright = placeholder.Value;
                        break;
                    case Placeholders.Description:
                        metadata.Description = placeholder.Value;
                        break;
                }
            }

            if ("http://LICENSE_URL_HERE_OR_DELETE_THIS_LINE" == metadata.LicenseUrl)
            {
                metadata.LicenseUrl = null;
            }

            if ("http://PROJECT_URL_HERE_OR_DELETE_THIS_LINE" == metadata.ProjectUrl)
            {
                metadata.ProjectUrl = null;
            }

            if ("http://ICON_URL_HERE_OR_DELETE_THIS_LINE" == metadata.IconUrl)
            {
                metadata.IconUrl = null;
            }

            if ("Summary of changes made in this release of the package." == metadata.ReleaseNotes)
            {
                metadata.ReleaseNotes = null;
            }

            if ("Tag1 Tag2" == metadata.Tags)
            {
                metadata.Tags = null;
            }

            return values;
        }

        private IEnumerable<PhysicalPackageFile> GetSourceFiles(AssemblyDefinition assembly)
        {
            var debuggable = assembly.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(DebuggableAttribute).Name);

            if (debuggable != null)
            {
                bool isJitTrackingEnabled = false;
                try
                {
                    if (debuggable.ConstructorArguments.Count == 1)
                    {
                        var modes = (DebuggableAttribute.DebuggingModes) debuggable.ConstructorArguments[0].Value;
                        isJitTrackingEnabled = new DebuggableAttribute(modes).IsJITTrackingEnabled;
                    }
                    else if (debuggable.ConstructorArguments.Count == 2)
                    {
                        isJitTrackingEnabled = (bool) debuggable.ConstructorArguments[0].Value;
                    }
                }
                catch (NotSupportedException)
                {
                    // skip
                }

                // If there's no JIT tracking then ignore the source symbols
                if (isJitTrackingEnabled)
                {
                    var files = new HashSet<string>();
                    foreach (var type in assembly.MainModule.Types)
                    {
                        AddSourceSymbolFiles(files, type);

                        foreach (var t in type.NestedTypes)
                        {
                            AddSourceSymbolFiles(files, t);
                        }
                    }

                    string root = null;
                    foreach (var file in files)
                    {
                        root = root != null
                            ? CommonPrefixWith(root, file)
                            : file;
                    }

                    if (!string.IsNullOrEmpty(root))
                    {
                        var list = new List<PhysicalPackageFile>();
                        foreach (var file in files)
                        {
                            if (!File.Exists(file))
                                continue;

                            var targetPath = "src\\" + file.Substring(root.Length);
                            list.Add(new PhysicalPackageFile(false, file, targetPath));
                        }

                        return list;
                    }
                }
            }

            return Enumerable.Empty<PhysicalPackageFile>();
        }

        private static string CommonPrefixWith(string left, string right)
        {
            if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
            {
                int maxLength = Math.Min(left.Length, right.Length);
                int length = 0;
                while (length < maxLength && left[length] == right[length])
                    ++length;

                if (0 < length)
                    return left.Substring(0, length);
            }

            return null;
        }

        private static void AddSourceSymbolFiles(HashSet<string> files, TypeDefinition type)
        {
            foreach (var m in type.Methods)
            {
                AddSourceSymbolFiles(files, m);
            }

            foreach (var p in type.Properties)
            {
                AddSourceSymbolFiles(files, p.GetMethod);
                AddSourceSymbolFiles(files, p.SetMethod);

                foreach (var m in p.OtherMethods)
                {
                    AddSourceSymbolFiles(files, m);
                }
            }

            foreach (var e in type.Events)
            {
                AddSourceSymbolFiles(files, e.AddMethod);
                AddSourceSymbolFiles(files, e.RemoveMethod);

                foreach (var m in e.OtherMethods)
                {
                    AddSourceSymbolFiles(files, m);
                }
            }
        }

        private static void AddSourceSymbolFiles(HashSet<string> files, MethodDefinition method)
        {
            if (method == null || !method.HasBody)
                return;

            foreach (var i in method.Body.Instructions)
            {
                if (i.SequencePoint == null)
                    continue;

                files.Add(i.SequencePoint.Document.Url);
            }
        }

        private static void TryResolveAssemblyId(Dictionary<string, string> values, IPackageFile file)
        {
            string id;
            if (values.TryGetValue(Placeholders.Id, out id))
            {
                var resolvedId = Path.GetFileNameWithoutExtension(file.Path);
                if (id == null)
                {
                    values[Placeholders.Id] = resolvedId;
                }
                else if (resolvedId != id)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture,
                            "Assembly '{0}' has different name '{1}', but expected '{2}'.",
                            file.Path, resolvedId, id));
                }
            }
        }

        private static void TryResolveAssemblyVersion(Dictionary<string, string> values, AssemblyDefinition assembly, IPackageFile file)
        {
            string version;
            if (values.TryGetValue(Placeholders.Version, out version))
            {
                var resolvedVersion = ResolveAssemblyVersion(assembly);
                if (version == null)
                {
                    values[Placeholders.Version] = resolvedVersion;
                }
                else if (resolvedVersion != version)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture,
                            "Assembly '{0}' has different version '{1}', but expected '{2}'.",
                            file.Path, resolvedVersion, version));
                }
            }
        }

        private static void TryResolveAssemblyTitle(Dictionary<string, string> values, AssemblyDefinition assembly, IPackageFile file)
        {
            string title;
            if (values.TryGetValue(Placeholders.Title, out title))
            {
                var resolvedTitle = ResolveAssemblyTitle(assembly);
                if (title == null)
                {
                    values[Placeholders.Title] = resolvedTitle;
                }
                else if (resolvedTitle != title)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture,
                            "Assembly '{0}' has different title '{1}', but expected '{2}'.",
                            file.Path, resolvedTitle, title));
                }
            }
        }

        private static void TryResolveAssemblyAuthor(Dictionary<string, string> values, AssemblyDefinition assembly, IPackageFile file)
        {
            string author;
            if (values.TryGetValue(Placeholders.Author, out author))
            {
                var resolvedAuthor = ResolveAssemblyAuthor(assembly);
                if (author == null)
                {
                    values[Placeholders.Author] = resolvedAuthor;
                }
                else if (resolvedAuthor != author)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture,
                            "Assembly '{0}' has different author '{1}', but expected '{2}'.",
                            file.Path, resolvedAuthor, author));
                }
            }
        }

        private static void TryResolveAssemblyCopyright(Dictionary<string, string> values, AssemblyDefinition assembly, IPackageFile file)
        {
            string description;
            if (values.TryGetValue(Placeholders.Copyright, out description))
            {
                var resolvedDescription = ResolveAssemblyCopyright(assembly);
                if (description == null)
                {
                    values[Placeholders.Copyright] = resolvedDescription;
                }
                else if (resolvedDescription != description)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture,
                            "Assembly '{0}' has different copyright '{1}', but expected '{2}'.",
                            file.Path, resolvedDescription, description));
                }
            }
        }

        private static void TryResolveAssemblyDescription(Dictionary<string, string> values, AssemblyDefinition assembly, IPackageFile file)
        {
            string description;
            if (values.TryGetValue(Placeholders.Description, out description))
            {
                var resolvedDescription = ResolveAssemblyDescription(assembly);
                if (description == null)
                {
                    values[Placeholders.Description] = resolvedDescription;
                }
                else if (resolvedDescription != description)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture,
                            "Assembly '{0}' has different description '{1}', but expected '{2}'.",
                            file.Path, resolvedDescription, description));
                }
            }
        }

        private static string ResolveAssemblyVersion(AssemblyDefinition assembly)
        {
            string result;
            var aiva = assembly.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyInformationalVersionAttribute).Name);

            if (aiva != null)
            {
                result = Convert.ToString(aiva.ConstructorArguments[0].Value);

                var semanticVersion = SemanticVersion.Parse(result);
                var sv = semanticVersion.Version;
                var av = assembly.Name.Version;
                if (!string.IsNullOrEmpty(semanticVersion.SpecialVersion) &&
                    sv.Major == av.Major && sv.Minor == av.Minor)
                {
                    // From MSDN:
                    // When specifying a version, you have to at least specify major.
                    // If you specify major and minor, you can specify an asterisk (*) for build.
                    // This will cause build to be equal to the number of days since January 1, 2000 local time,
                    // and for revision to be equal to the number of seconds since midnight local time
                    // (without taking into account time zone adjustments for daylight saving time), divided by 2.

                    if (av.Build != 0 && sv.Build == 0)
                    {
                        // This is Auto incremented build number?
                        var sinceY2K = TimeSpan.FromDays(av.Build);
                        if ((DateTime.Now - sinceY2K).Year == 2000)
                        {
                            result += "-" + av.Build;
                        }
                    }

                    if (av.Revision != 0 && sv.Revision == 0)
                    {
                        // This is Auto incremented revision number?
                        var sinceMidnight = TimeSpan.FromSeconds(av.Revision*2);
                        if ((DateTime.Now - sinceMidnight).Hour <= 4)
                        {
                            result += "-" + (av.Revision / 60);
                        }
                    }
                }
            }
            else
            {
                result = assembly.Name.Version.ToString();
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string ResolveAssemblyTitle(AssemblyDefinition assembly)
        {
            string result = null;
            var ata = assembly.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyTitleAttribute).Name);
            if (ata != null)
            {
                result = Convert.ToString(ata.ConstructorArguments[0].Value);
            }
            else
            {
                var apa = assembly.CustomAttributes.FirstOrDefault(
                    a => a.AttributeType.Name == typeof(AssemblyProductAttribute).Name);
                if (apa != null)
                {
                    result = Convert.ToString(apa.ConstructorArguments[0].Value);
                }
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string ResolveAssemblyAuthor(AssemblyDefinition assembly)
        {
            string result = null;
            var aca = assembly.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyCompanyAttribute).Name);
            if (aca != null)
            {
                result = Convert.ToString(aca.ConstructorArguments[0].Value);
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string ResolveAssemblyCopyright(AssemblyDefinition assembly)
        {
            string result = null;
            var ada = assembly.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyCopyrightAttribute).Name);
            if (ada != null)
            {
                result = Convert.ToString(ada.ConstructorArguments[0].Value);
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string ResolveAssemblyDescription(AssemblyDefinition assembly)
        {
            string result = null;
            var ada = assembly.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyDescriptionAttribute).Name);
            if (ada != null)
            {
                result = Convert.ToString(ada.ConstructorArguments[0].Value);
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private void AddFiles(Manifest manifest, string basePath)
        {
            // If there's no base path then ignore the files node
            if (basePath == null)
                return;

            if (manifest.Files == null)
            {
                AddFiles(basePath, @"**\*.*", null);
            }
            else
            {
                foreach (ManifestFile file in manifest.Files)
                {
                    AddFiles(basePath, file.Source, file.Target, file.Exclude);
                }
            }
        }

        private void WriteManifest(Package package, int minimumManifestVersion)
        {
            Uri uri = UriUtility.CreatePartUri(Id + Constants.ManifestExtension);

            // Create the manifest relationship
            package.CreateRelationship(uri, TargetMode.Internal,
                                       Constants.PackageRelationshipNamespace + ManifestRelationType);

            // Create the part
            PackagePart packagePart = package.CreatePart(uri, DefaultContentType, CompressionOption.Maximum);

            using (Stream stream = packagePart.GetStream())
            {
                Manifest manifest = Manifest.Create(this);
                //if (PackageAssemblyReferences.Any())
                //{
                //    manifest.Metadata.References = new List<ManifestReference>(
                //        PackageAssemblyReferences.Select(reference => new ManifestReference {File = reference.File}));
                //}
                manifest.Save(stream, minimumManifestVersion);
            }
        }

        private void WriteFiles(Package package)
        {
            // Add files that might not come from expanding files on disk
            foreach (IPackageFile file in new HashSet<IPackageFile>(Files))
            {
                using (Stream stream = file.GetStream())
                {
                    CreatePart(package, file.Path, stream);
                }
            }
        }

        private void AddFiles(string basePath, string source, string destination, string exclude = null)
        {
            string fileName = Path.GetFileName(source);

            // treat empty files specially
            if (Constants.PackageEmptyFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                string destinationPath = Path.GetDirectoryName(destination);
                if (String.IsNullOrEmpty(destinationPath))
                {
                    destinationPath = Path.GetDirectoryName(source);
                }
                Files.Add(new EmptyFolderFile(destinationPath));
            }
            else
            {
                List<PackageFileBase> searchFiles = PathResolver.ResolveSearchPattern(basePath, source, destination).ToList();
                ExcludeFiles(searchFiles, basePath, exclude);

                if (!PathResolver.IsWildcardSearch(source) && !PathResolver.IsDirectoryPath(source) && !searchFiles.Any())
                {
                    throw new FileNotFoundException(String.Format(CultureInfo.CurrentCulture,
                                                                  NuGetResources.PackageAuthoring_FileNotFound,
                                                                  source));
                }

                Files.AddRange(searchFiles);
            }
        }

        private static void ExcludeFiles(List<PackageFileBase> searchFiles, string basePath, string exclude)
        {
            if (String.IsNullOrEmpty(exclude))
            {
                return;
            }

            // One or more exclusions may be specified in the file. Split it and prepend the base path to the wildcard provided.
            string[] exclusions = exclude.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string item in exclusions)
            {
                string wildCard = PathResolver.NormalizeWildcard(basePath, item);
                PathResolver.FilterPackageFiles(searchFiles, p => p.OriginalPath, new[] { wildCard });
            }
        }

        private static void CreatePart(Package package, string path, Stream sourceStream)
        {
            if (PackageUtility.IsManifest(path))
            {
                return;
            }

            Uri uri = UriUtility.CreatePartUri(path);

            // Create the part
            PackagePart packagePart = package.CreatePart(uri, DefaultContentType, CompressionOption.Maximum);
            using (Stream stream = packagePart.GetStream())
            {
                sourceStream.CopyTo(stream);
            }
        }

        /// <summary>
        /// Tags come in this format. tag1 tag2 tag3 etc..
        /// </summary>
        private static IEnumerable<string> ParseTags(string tags)
        {
            Debug.Assert(tags != null);
            return from tag in tags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                   select tag.Trim();
        }

        public IPackage Build()
        {
            if (_sourceFiles.Count > 0)
                throw new InvalidOperationException("Auto populated source files not commited");

            return new SimplePackage(this);
        }

        public void AcceptSourceFilesAutoPopulation()
        {
            Files.AddRange(_sourceFiles);
            _sourceFiles.Clear();
        }

        public void RejectSourceFilesAutoPopulate()
        {
            _sourceFiles.Clear();
        }
    }
}