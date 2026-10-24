// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace osu.Framework.Configuration
{
    public class IniConfigManager<TLookup> : ConfigManager<TLookup>
        where TLookup : struct, Enum
    {
        /// <summary>
        /// The backing file used to store the config. Null means no persistent storage.
        /// </summary>
        protected virtual string Filename => @"game.ini";

        private readonly Storage storage;

        // TumThai: preserve keys in game.ini / framework.ini that are not known to this build's enum,
        // so switching between TumThai and TumThai!Next builds does not wipe unknown settings.
        private readonly Dictionary<string, string> preservedUnknownKeys = new Dictionary<string, string>();

        public IniConfigManager(Storage storage, IDictionary<TLookup, object> defaultOverrides = null)
            : base(defaultOverrides)
        {
            this.storage = storage;

            InitialiseDefaults();
            Load();
        }

        protected override void PerformLoad()
        {
            if (string.IsNullOrEmpty(Filename)) return;

            using (var stream = storage.GetStream(Filename))
            {
                if (stream == null)
                    return;

                using (var reader = new StreamReader(stream))
                {
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        int equalsIndex = line.IndexOf('=');

                        if (line.Length == 0 || line[0] == '#' || equalsIndex < 0) continue;

                        string key = line.AsSpan(0, equalsIndex).Trim().ToString();
                        string val = line.AsSpan(equalsIndex + 1).Trim().ToString();

                        if (!Enum.TryParse(key, out TLookup lookup))
                        {
                            preservedUnknownKeys[key] = val;
                            continue;
                        }

                        if (ConfigStore.TryGetValue(lookup, out IBindable b))
                        {
                            try
                            {
                                if (!(b is IParseable parseable))
                                    throw new InvalidOperationException($"Bindable type {b.GetType().ReadableName()} is not {nameof(IParseable)}.");

                                parseable.Parse(val, CultureInfo.InvariantCulture);
                            }
                            catch (Exception e)
                            {
                                Logger.Log($@"Unable to parse config key {lookup}: {e}", LoggingTarget.Runtime, LogLevel.Important);
                            }
                        }
                        else if (AddMissingEntries)
                            SetDefault(lookup, val);
                    }
                }
            }
        }

        /// <summary>
        /// How a setting's value is written to the file (by default its invariant string). TumThai!Next: lets a value
        /// only this framework has be written as one stock osu!framework reads (the same file is shared with TumThai).
        /// </summary>
        protected virtual string SerialiseValue(TLookup lookup, IBindable bindable) => bindable.ToString(CultureInfo.InvariantCulture);

        protected override bool PerformSave()
        {
            if (string.IsNullOrEmpty(Filename)) return false;

            try
            {
                using (var stream = storage.CreateFileSafely(Filename))
                using (var w = new StreamWriter(stream))
                {
                    foreach (var p in ConfigStore)
                        w.WriteLine(@"{0} = {1}", p.Key, SerialiseValue(p.Key, p.Value).AsNonNull().Replace("\n", "").Replace("\r", ""));

                    foreach (var p in preservedUnknownKeys)
                        w.WriteLine(@"{0} = {1}", p.Key, p.Value.Replace("\n", "").Replace("\r", ""));
                }
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}
