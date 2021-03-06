﻿using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace NuGet
{
    public abstract class PackageFileBase : IPackageFile
    {
        private readonly FrameworkName _targetFramework;

        protected PackageFileBase(string path)
        {
            Path = path;

            string effectivePath;
            _targetFramework = VersionUtility.ParseFrameworkNameFromFilePath(path, out effectivePath);
            EffectivePath = effectivePath;
        }

        public string Path
        {
            get;
            private set;
        }

        public virtual string OriginalPath
        {
            get
            {
                return null;
            }
        }

        public abstract Stream GetStream();

        public string EffectivePath
        {
            get;
            private set;
        }

        public FrameworkName TargetFramework
        {
            get
            {
                return _targetFramework;
            }
        }

        public IEnumerable<FrameworkName> SupportedFrameworks
        {
            get
            {
                if (TargetFramework != null)
                {
                    yield return TargetFramework;
                }
                yield break;
            }
        }
    }
}
