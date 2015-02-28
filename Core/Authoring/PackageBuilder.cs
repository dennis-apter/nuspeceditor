using System;
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

        public PackageBuilder(string path)
            : this(path, Path.GetDirectoryName(path))
        {
        }

        public PackageBuilder(string path, string basePath)
            : this()
        {
            using (Stream stream = File.OpenRead(path))
            {
                ReadManifest(stream, basePath);
            }
        }

        public PackageBuilder(Stream stream, string basePath)
            : this()
        {
            ReadManifest(stream, basePath);
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

        public IDictionary<string,string> TemplateValues
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

        private void ReadManifest(Stream stream, string basePath)
        {
            // Deserialize the document and extract the metadata
            Manifest manifest = Manifest.ReadFrom(stream);

            bool filesAdded = false;
            if (manifest.IsTemplate)
            {
                AddFiles(manifest, basePath);
                filesAdded = true;

                FillMetadataTemplate(manifest.Metadata);
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

        private void FillMetadataTemplate(ManifestMetadata metadata)
        {
            var values = new Dictionary<string, string>();
            if (Placeholders.Id.Equals(metadata.Id, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Id, null);

            if (Placeholders.Version.Equals(metadata.Version, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Version, null);

            if (Placeholders.Title.Equals(metadata.Title, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Title, null);

            if (Placeholders.Author.Equals(metadata.Authors, StringComparison.InvariantCultureIgnoreCase) ||
                Placeholders.Author.Equals(metadata.Owners, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Author, null);

            if (Placeholders.Description.Equals(metadata.Description, StringComparison.InvariantCultureIgnoreCase))
                values.Add(Placeholders.Description, null);

            bool resolved = false;
            foreach (var file in Files)
            {
                if (!file.Path.StartsWith("lib") ||
                    !".dll".Equals(Path.GetExtension(file.Path), StringComparison.InvariantCultureIgnoreCase))
                    continue;

                var assemblyDefinition = AssemblyDefinition.ReadAssembly(file.OriginalPath);

                TryResolveAssemblyId(values, file);
                TryResolveAssemblyVersion(values, assemblyDefinition, file);
                TryResolveAssemblyTitle(values, assemblyDefinition, file);
                TryResolveAssemblyAuthor(values, assemblyDefinition, file);
                TryResolveAssemblyDescription(values, assemblyDefinition, file);

                resolved = true;
            }
            
            var unresolvedPlaceholders = values.Where(p => p.Value == null).Select(p => p.Key).ToList();
            if (!resolved || unresolvedPlaceholders.Count > 0)
            {
                throw new ArgumentException(string.Format("Can't resolve template values ({0})", string.Join(", ", unresolvedPlaceholders)));
            }

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

            TemplateValues = new ReadOnlyDictionary<string,string>(values);
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

        private static void TryResolveAssemblyVersion(Dictionary<string, string> values, AssemblyDefinition assemblyDefinition, IPackageFile file)
        {
            string version;
            if (values.TryGetValue(Placeholders.Version, out version))
            {
                var resolvedVersion = ResolveAssemblyVersion(assemblyDefinition);
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

        private static void TryResolveAssemblyTitle(Dictionary<string, string> values, AssemblyDefinition assemblyDefinition, IPackageFile file)
        {
            string title;
            if (values.TryGetValue(Placeholders.Title, out title))
            {
                var resolvedTitle = ResolveAssemblyTitle(assemblyDefinition);
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

        private static void TryResolveAssemblyAuthor(Dictionary<string, string> values, AssemblyDefinition assemblyDefinition, IPackageFile file)
        {
            string author;
            if (values.TryGetValue(Placeholders.Author, out author))
            {
                var resolvedAuthor = ResolveAssemblyAuthor(assemblyDefinition);
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

        private static void TryResolveAssemblyDescription(Dictionary<string, string> values, AssemblyDefinition assemblyDefinition, IPackageFile file)
        {
            string description;
            if (values.TryGetValue(Placeholders.Description, out description))
            {
                var resolvedDescription = ResolveAssemblyDescription(assemblyDefinition);
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

        private static string ResolveAssemblyVersion(AssemblyDefinition assemblyDefinition)
        {
            string result = null;
            var aiva = assemblyDefinition.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyInformationalVersionAttribute).Name);
            if (aiva != null)
            {
                result = Convert.ToString(aiva.ConstructorArguments[0].Value);
            }
            else
            {
                var ava = assemblyDefinition.CustomAttributes.FirstOrDefault(
                    a => a.AttributeType.Name == typeof(AssemblyVersionAttribute).Name);
                if (ava != null)
                {
                    result = Convert.ToString(ava.ConstructorArguments[0].Value);
                }
                else
                {
                    var afva = assemblyDefinition.CustomAttributes.FirstOrDefault(
                        a => a.AttributeType.Name == typeof(AssemblyFileVersionAttribute).Name);
                    if (afva != null)
                    {
                        result = Convert.ToString(afva.ConstructorArguments[0].Value);
                    }
                }
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string ResolveAssemblyTitle(AssemblyDefinition assemblyDefinition)
        {
            string result = null;
            var ata = assemblyDefinition.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyTitleAttribute).Name);
            if (ata != null)
            {
                result = Convert.ToString(ata.ConstructorArguments[0].Value);
            }
            else
            {
                var apa = assemblyDefinition.CustomAttributes.FirstOrDefault(
                    a => a.AttributeType.Name == typeof(AssemblyProductAttribute).Name);
                if (apa != null)
                {
                    result = Convert.ToString(apa.ConstructorArguments[0].Value);
                }
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string ResolveAssemblyAuthor(AssemblyDefinition assemblyDefinition)
        {
            string result = null;
            var aca = assemblyDefinition.CustomAttributes.FirstOrDefault(
                a => a.AttributeType.Name == typeof(AssemblyCompanyAttribute).Name);
            if (aca != null)
            {
                result = Convert.ToString(aca.ConstructorArguments[0].Value);
            }

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string ResolveAssemblyDescription(AssemblyDefinition assemblyDefinition)
        {
            string result = null;
            var ada = assemblyDefinition.CustomAttributes.FirstOrDefault(
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
            return new SimplePackage(this);
        }
    }
}