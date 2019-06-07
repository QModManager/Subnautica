﻿namespace QModManager.API.ModLoading.Internal
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Oculus.Newtonsoft.Json;
    using QModManager.DataStructures;
    using QModManager.Utility;

    [JsonObject(MemberSerialization.OptIn)]
    internal class QMod : IQMod, IQModSerialiable, IQModLoadable, ISortable<string>
    {
        /// <summary>
        /// The dummy <see cref="QMod"/> which is used to represent QModManager
        /// </summary>
        internal static QMod QModManager { get; } = new QMod
        {
            Id = "QModManager",
            DisplayName = "QModManager",
            Author = "QModManager Dev Team",
            LoadedAssembly = Assembly.GetExecutingAssembly(),
            ParsedGame = QModGame.Both,
            Enable = true
        };

        private Assembly _loadedAssembly;
        private Version _modVersion;
        private QModGame _moddedGame = QModGame.None;
        private Dictionary<string, Version> _requiredMods;

        public QMod()
        {
            // Empty public constructor for JSON
        }

        internal QMod(QModCoreInfo modInfo, Type originatingType, Assembly loadedAssembly, string subDirectory)
        {
            this.ModDirectory = subDirectory;

            // Basic mod info
            this.Id = modInfo.Id;
            this.DisplayName = modInfo.DisplayName;
            this.Author = modInfo.Author;
            this.ParsedGame = modInfo.SupportedGame;

            // Dependencies
            this.RequiredMods = GetDependencies(originatingType);

            // Load order
            this.LoadBefore = GetOrderedMods<QModLoadBefore>(originatingType);
            this.LoadAfter = GetOrderedMods<QModLoadAfter>(originatingType);

            // Patch methods
            this.PatchMethods = GetPatchMethods(originatingType);

            // Assembly info
            this.LoadedAssembly = loadedAssembly;
        }

        internal QMod(string name)
        {
            this.Id = Patcher.IDRegex.Replace(name, "");
            this.DisplayName = name;
            this.Author = "Unknown";
            this.ParsedGame = QModGame.None;
            this.Enable = false;
        }

        [JsonProperty(Required = Required.Always)]
        public string Id { get; set; }

        [JsonProperty(Required = Required.Always)]
        public string DisplayName { get; set; }

        [JsonProperty(Required = Required.Always)]
        public string Author { get; set; }

        [JsonProperty(Required = Required.Always)]
        public string Version { get; set; }

        [JsonProperty(Required = Required.Default)]
        public string[] Dependencies { get; set; } = new string[0];

        [JsonProperty(Required = Required.Default)]
        public Dictionary<string, string> VersionDependencies { get; set; } = new Dictionary<string, string>();

        [JsonProperty(Required = Required.Default)]
        public string[] LoadBefore { get; set; } = new string[0];

        [JsonProperty(Required = Required.Default)]
        public string[] LoadAfter { get; set; } = new string[0];

        [JsonProperty(Required = Required.DisallowNull)]
        public string Game { get; set; } = $"{QModGame.Subnautica}";

        [JsonProperty(Required = Required.Always)]
        public string AssemblyName { get; set; }

        [JsonProperty(Required = Required.DisallowNull, DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
        public bool Enable { get; set; } = true;

        [JsonProperty(Required = Required.Always)]
        public string EntryMethod { get; set; }

        public string ModDirectory { get; private set; }

        public Dictionary<string, Version> RequiredMods
        {
            get => _requiredMods;
            private set
            {
                _requiredMods = value;
                this.Dependencies = new string[_requiredMods.Count];

                int i = 0;
                foreach (KeyValuePair<string, Version> dependency in _requiredMods)
                {
                    this.Dependencies[i] = dependency.Key;

                    if (!IsDefaultVersion(dependency.Value))
                        this.VersionDependencies.Add(dependency.Key, dependency.Value.ToStringParsed());

                    i++;
                }
            }
        }

        public QModGame ParsedGame
        {
            get => _moddedGame;
            private set
            {
                _moddedGame = value;
                this.Game = $"{_moddedGame}";
            }
        }

        public bool IsLoaded
        {
            get
            {
                if (this.PatchMethods.Count == 0)
                    return false;

                foreach (PatchMethod patchingMethod in this.PatchMethods.Values)
                {
                    if (!patchingMethod.IsPatched)
                        return false;
                }

                return true;
            }
        }

        public Assembly LoadedAssembly
        {
            get => _loadedAssembly;
            set
            {
                _loadedAssembly = value;

                AssemblyName assemblyName = _loadedAssembly.GetName();
                this.AssemblyName = assemblyName.Name;
                this.ParsedVersion = assemblyName.Version;
            }
        }

        public Version ParsedVersion
        {
            get => _modVersion;
            private set
            {
                _modVersion = value;
                this.Version = _modVersion.ToStringParsed();
            }
        }

        public Dictionary<PatchingOrder, PatchMethod> PatchMethods { get; } = new Dictionary<PatchingOrder, PatchMethod>();

        public ICollection<string> DependencyCollection { get; private set; }
        public ICollection<string> LoadBeforeCollection { get; private set; }
        public ICollection<string> LoadAfterCollection { get; private set; }

        public ModLoadingResults TryLoading(PatchingOrder order, QModGame currentGame)
        {
            if ((this.ParsedGame & currentGame) == QModGame.None)
            {
                this.PatchMethods.Clear(); // Do not attempt any other patch methods
                return ModLoadingResults.CurrentGameNotSupported;
            }

            if (!this.PatchMethods.TryGetValue(order, out PatchMethod patchMethod))
                return ModLoadingResults.NoMethodToExecute;

            if (patchMethod.IsPatched)
                return ModLoadingResults.AlreadyLoaded;

            PatchResults result = patchMethod.TryInvoke();
            switch (result)
            {
                case PatchResults.OK:
                    Logger.Info($"Loaded mod \"{this.Id}\" at {order}");
                    return ModLoadingResults.Success;
                case PatchResults.Error:
                    this.PatchMethods.Clear(); // Do not attempt any other patch methods
                    return ModLoadingResults.Failure;
                case PatchResults.ModderCanceled:
                    return ModLoadingResults.CancledByModAuthor;
            }

            return ModLoadingResults.Failure;
        }

        public ModStatus TryCompletingJsonLoading(string subDirectory)
        {
            switch (this.Game)
            {
                case "BelowZero":
                    _moddedGame = QModGame.BelowZero;
                    break;
                case "Both":
                    _moddedGame = QModGame.Both;
                    break;
                case "Subnautica":
                    _moddedGame = QModGame.Subnautica;
                    break;
                default:
                    return ModStatus.FailedIdentifyingGame;
            }

            try
            {
                this.ParsedVersion = new Version(this.Version);
            }
            catch (Exception vEx)
            {
                Logger.Error($"There was an error parsing version \"{this.Version}\" for mod \"{this.DisplayName}\"");
                Logger.Exception(vEx);

                return ModStatus.MissingCoreData;
            }

            string modAssemblyPath = Path.Combine(subDirectory, this.AssemblyName);

            if (string.IsNullOrEmpty(modAssemblyPath) || !File.Exists(modAssemblyPath))
            {
                Logger.Error($"No matching dll found at \"{modAssemblyPath}\" for mod \"{this.DisplayName}\"");
                return ModStatus.MissingAssemblyFile;
            }
            else
            {
                try
                {
                    this.LoadedAssembly = Assembly.LoadFrom(modAssemblyPath);
                }
                catch (Exception aEx)
                {
                    Logger.Error($"Failed loading the dll found at \"{modAssemblyPath}\" for mod \"{this.DisplayName}\"");
                    Logger.Exception(aEx);
                    return ModStatus.FailedLoadingAssemblyFile;
                }
            }

            MethodInfo patchMethod = GetPatchMethod(this.EntryMethod, this.LoadedAssembly);

            if (patchMethod != null)
                this.PatchMethods.Add(PatchingOrder.NormalInitialize, new PatchMethod(patchMethod, this));

            if (this.PatchMethods.Count == 0)
                return ModStatus.MissingPatchMethod;

            this.DependencyCollection = new HashSet<string>(this.Dependencies);
            this.LoadBeforeCollection = new HashSet<string>(this.LoadBefore);
            this.LoadAfterCollection = new HashSet<string>(this.LoadAfter);

            return ModStatus.Success;
        }

        private Dictionary<string, Version> GetDependencies(Type originatingType)
        {
            var dependencies = (QModDependency[])originatingType.GetCustomAttributes(typeof(QModDependency), false);
            var dictionary = new Dictionary<string, Version>();
            foreach (QModDependency dependency in dependencies)
            {
                dictionary.Add(dependency.RequiredMod, dependency.MinimumVersion);
            }

            return dictionary;
        }

        private string[] GetOrderedMods<T>(Type originatingType) where T : IModOrder
        {
            object[] others = originatingType.GetCustomAttributes(typeof(T), false);

            int length = others.Length;
            string[] array = new string[length];

            for (int i = 0; i < length; i++)
                array[i] = (others[i] as IModOrder).OtherMod;

            return array;
        }

        private Dictionary<PatchingOrder, PatchMethod> GetPatchMethods(Type originatingType)
        {
            var dictionary = new Dictionary<PatchingOrder, PatchMethod>(3);

            MethodInfo[] methods = originatingType.GetMethods(BindingFlags.Public);
            foreach (MethodInfo method in methods)
            {
                object[] patchMethods = method.GetCustomAttributes(typeof(QModPatchAttributeBase), false);
                foreach (QModPatchAttributeBase patchmethod in patchMethods)
                {
                    if (dictionary.ContainsKey(patchmethod.PatchOrder))
                    {
                        // Duplicate method found
                        dictionary[patchmethod.PatchOrder] = null;
                        return dictionary;
                    }

                    dictionary.Add(patchmethod.PatchOrder, new PatchMethod(method, this));
                }
            }

            return dictionary;
        }

        private bool IsDefaultVersion(Version version)
        {
            return version.Major == 0 && version.Minor == 0 && version.Revision == 0 && version.Build == 0;
        }

        private MethodInfo GetPatchMethod(string methodPath, Assembly assembly)
        {
            string[] entryMethodSig = methodPath.Split('.');
            string entryType = string.Join(".", entryMethodSig.Take(entryMethodSig.Length - 1).ToArray());
            string entryMethod = entryMethodSig[entryMethodSig.Length - 1];

            return assembly.GetType(entryType).GetMethod(entryMethod);
        }

    }
}
