﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Exceptionless.Dependency;
using Exceptionless.Diagnostics;
using Exceptionless.Enrichments.Default;
using Exceptionless.Extras;
using Exceptionless.Extras.Storage;
using Exceptionless.Logging;
using Exceptionless.Storage;

namespace Exceptionless {
    public static class ExceptionlessExtraConfigurationExtensions {
        /// <summary>
        /// Reads the Exceptionless configuration from the app.config or web.config file.
        /// </summary>
        /// <param name="config">The configuration object you want to apply the attribute settings to.</param>
        public static void UseErrorEnrichment(this ExceptionlessConfiguration config) {
            config.RemoveEnrichment<SimpleErrorEnrichment>();
            config.AddEnrichment<Enrichments.ErrorEnrichment>();
        }

        public static void UseIsolatedStorage(this ExceptionlessConfiguration config) {
            config.Resolver.Register<IObjectStorage, IsolatedStorageObjectStorage>();
        }

        public static void UseFolderStorage(this ExceptionlessConfiguration config, string folder) {
            config.Resolver.Register<IObjectStorage>(new FolderObjectStorage(config.Resolver, folder));
        }

        public static void UseTraceLogger(this ExceptionlessConfiguration config, LogLevel minLogLevel = LogLevel.Info) {
            config.Resolver.Register<IExceptionlessLog>(new TraceExceptionlessLog { MinimumLogLevel = minLogLevel });
        }

        public static void UseFileLogger(this ExceptionlessConfiguration config, string logPath, LogLevel minLogLevel = LogLevel.Info) {
            var log = new SafeExceptionlessLog(new FileExceptionlessLog(logPath)) { MinimumLogLevel = minLogLevel };
            config.Resolver.Register<IExceptionlessLog>(log);
        }

        public static void UseIsolatedStorageLogger(this ExceptionlessConfiguration config, LogLevel minLogLevel = LogLevel.Info) {
            var log = new SafeExceptionlessLog(new IsolatedStorageFileExceptionlessLog("exceptionless.log")) { MinimumLogLevel = minLogLevel };
            config.Resolver.Register<IExceptionlessLog>(log);
        }

        public static void UseTraceLogEntriesEnrichment(this ExceptionlessConfiguration config, int? defaultMaxEntriesToInclude = null) {
            int maxEntriesToInclude = config.Settings.GetInt32(TraceLogEnrichment.MaxEntriesToIncludeKey, defaultMaxEntriesToInclude ?? 0);

            if (!Trace.Listeners.OfType<ExceptionlessTraceListener>().Any())
                Trace.Listeners.Add(new ExceptionlessTraceListener(maxEntriesToInclude));

            if (!config.Settings.ContainsKey(TraceLogEnrichment.MaxEntriesToIncludeKey) && defaultMaxEntriesToInclude.HasValue)
                config.Settings.Add(TraceLogEnrichment.MaxEntriesToIncludeKey, maxEntriesToInclude.ToString());

            config.AddEnrichment(typeof(TraceLogEnrichment).Name, c => new TraceLogEnrichment(c));
        }

        public static void ReadAllConfig(this ExceptionlessConfiguration config, params Assembly[] configAttributesAssemblies) {
            if (!config.Resolver.HasRegistration<IObjectStorage>())
                config.UseIsolatedStorage();

            if (configAttributesAssemblies == null || configAttributesAssemblies.Length == 0)
                config.ReadFromAttributes(Assembly.GetEntryAssembly(), Assembly.GetCallingAssembly());
            else
                config.ReadFromAttributes(configAttributesAssemblies);
            config.ReadFromConfigSection();
            config.ApplySavedServerSettings();
        }

        /// <summary>
        /// Reads the Exceptionless configuration from the app.config or web.config file.
        /// </summary>
        /// <param name="config">The configuration object you want to apply the attribute settings to.</param>
        public static void ReadFromConfigSection(this ExceptionlessConfiguration config) {
            ExceptionlessSection section = null;

            try {
                section = ConfigurationManager.GetSection("exceptionless") as ExceptionlessSection;
            } catch (Exception ex) {
                config.Resolver.GetLog().Error(typeof(ExceptionlessExtraConfigurationExtensions), ex, String.Concat("An error occurred while retrieving the configuration section. Exception: ", ex.Message));
            }

            if (section == null)
                return;

            config.Enabled = section.Enabled;

            // Only update if it is not null
            if (!String.IsNullOrEmpty(section.ApiKey))
                config.ApiKey = section.ApiKey;

            // If an appsetting is present for ApiKey, then it will override the other api keys
            string apiKeyOverride = ConfigurationManager.AppSettings["Exceptionless:ApiKey"] ?? String.Empty;
            if (!String.IsNullOrEmpty(apiKeyOverride))
                config.ApiKey = apiKeyOverride;

            if (!String.IsNullOrEmpty(section.ServerUrl))
                config.ServerUrl = section.ServerUrl;

            if (section.EnableSSL.HasValue)
                config.EnableSSL = section.EnableSSL.Value;

            if (!String.IsNullOrEmpty(section.StoragePath))
                config.UseFolderStorage(section.StoragePath);

            if (section.EnableLogging.HasValue && section.EnableLogging.Value) {
                if (!String.IsNullOrEmpty(section.LogPath))
                    config.UseFileLogger(section.LogPath);
                else if (!String.IsNullOrEmpty(section.StoragePath))
                    config.UseFileLogger(Path.Combine(section.StoragePath, "exceptionless.log"));
                else if (!config.Resolver.HasRegistration<IExceptionlessLog>())
                    config.UseIsolatedStorageLogger();
            }

            foreach (var tag in section.Tags.SplitAndTrim(',').Where(tag => !String.IsNullOrEmpty(tag)))
                config.DefaultTags.Add(tag);

            if (section.ExtendedData != null) {
                foreach (NameValueConfigurationElement setting in section.ExtendedData) {
                    if (!String.IsNullOrEmpty(setting.Name))
                        config.DefaultData[setting.Name] = setting.Value;
                }
            }

            if (section.Settings != null) {
                foreach (NameValueConfigurationElement setting in section.Settings) {
                    if (!String.IsNullOrEmpty(setting.Name))
                        config.Settings[setting.Name] = setting.Value;
                }
            }
        }

        public static void AddResolversFromConfig(this ExceptionlessConfiguration config)
        {
            ExceptionlessSection section = null;

            try {
                section = ConfigurationManager.GetSection("exceptionless") as ExceptionlessSection;
            } catch (Exception ex) {
                config.Resolver.GetLog().Error(typeof(ExceptionlessExtraConfigurationExtensions), ex, String.Concat("An error occurred while retrieving the configuration section. Exception: ", ex.Message));
            }

            if (section == null)
                return;

            if (section.Resolvers == null || section.Resolvers.Count == 0) {
                return;
            }

            foreach (ResolverConfigElement resolver in section.Resolvers) {
                if (!IsValidInterface(resolver.Interface)) {
                    config.Resolver.GetLog().Error(typeof(ExceptionlessExtraConfigurationExtensions), String.Concat("The provided resolver interface is not known: ", resolver.Interface));
                }
                Type resolverInterface = FindType(resolver.Interface);
                if (resolverInterface == null) {
                    config.Resolver.GetLog().Error(typeof(ExceptionlessExtraConfigurationExtensions), String.Concat("Huh - Internal error - Cannot find interface ", resolver.Interface));
                }
                try {
                    Type type = Type.GetType(resolver.Type);
                    config.Resolver.Register(resolverInterface, type);
                }
                catch (Exception ex)
                {
                    config.Resolver.GetLog().Error(typeof(ExceptionlessExtraConfigurationExtensions), ex, String.Format("An error occurred while retrieving a resolver for {0}. Exception: {1}", resolver.Interface, ex.Message));
                }
            }
        }

        private static bool IsValidInterface(string interfaceName) {
            return interfaceName == "ISubmissionClient" || interfaceName == "IDuplicateChecker"
                || interfaceName == "IEnvironmentInfoCollector" || interfaceName == "IEventQueue"
                || interfaceName == "IObjectStorage" || interfaceName == "IJsonSerializer"
                || interfaceName == "ILastReferenceIdManager" || interfaceName == "IExceptionlessLog";
        }

        /// <summary>
        /// Looks in all loaded assemblies for the given type.
        /// </summary>
        /// <param name="name">
        /// The name or full name of the type.
        /// </param>
        /// <returns>
        /// The <see cref="Type"/> found; null if not found.
        /// </returns>
        /// <references>based on http://stackoverflow.com/a/20862223/2298807</references>
        private static Type FindType(string name)
        {
            return
                AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.Name.Equals(name) || t.FullName.Equals(name));
        }
    }
}
