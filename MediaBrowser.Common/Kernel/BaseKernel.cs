﻿using MediaBrowser.Common.Events;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.ScheduledTasks;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Common.Kernel
{
    /// <summary>
    /// Represents a shared base kernel for both the Ui and server apps
    /// </summary>
    /// <typeparam name="TConfigurationType">The type of the T configuration type.</typeparam>
    /// <typeparam name="TApplicationPathsType">The type of the T application paths type.</typeparam>
    public abstract class BaseKernel<TConfigurationType, TApplicationPathsType> : IDisposable, IKernel
        where TConfigurationType : BaseApplicationConfiguration, new()
        where TApplicationPathsType : IApplicationPaths
    {
        /// <summary>
        /// Occurs when [has pending restart changed].
        /// </summary>
        public event EventHandler HasPendingRestartChanged;

        #region ConfigurationUpdated Event
        /// <summary>
        /// Occurs when [configuration updated].
        /// </summary>
        public event EventHandler<EventArgs> ConfigurationUpdated;

        /// <summary>
        /// Called when [configuration updated].
        /// </summary>
        internal void OnConfigurationUpdated()
        {
            EventHelper.QueueEventIfNotNull(ConfigurationUpdated, this, EventArgs.Empty, Logger);

            // Notify connected clients
            TcpManager.SendWebSocketMessage("ConfigurationUpdated", Configuration);
        }
        #endregion

        #region LoggerLoaded Event
        /// <summary>
        /// Fires whenever the logger is loaded
        /// </summary>
        public event EventHandler LoggerLoaded;
        /// <summary>
        /// Called when [logger loaded].
        /// </summary>
        private void OnLoggerLoaded()
        {
            EventHelper.QueueEventIfNotNull(LoggerLoaded, this, EventArgs.Empty, Logger);
        }
        #endregion

        #region ReloadBeginning Event
        /// <summary>
        /// Fires whenever the kernel begins reloading
        /// </summary>
        public event EventHandler<EventArgs> ReloadBeginning;
        /// <summary>
        /// Called when [reload beginning].
        /// </summary>
        private void OnReloadBeginning()
        {
            EventHelper.QueueEventIfNotNull(ReloadBeginning, this, EventArgs.Empty, Logger);
        }
        #endregion

        #region ReloadCompleted Event
        /// <summary>
        /// Fires whenever the kernel completes reloading
        /// </summary>
        public event EventHandler<EventArgs> ReloadCompleted;
        /// <summary>
        /// Called when [reload completed].
        /// </summary>
        private void OnReloadCompleted()
        {
            EventHelper.QueueEventIfNotNull(ReloadCompleted, this, EventArgs.Empty, Logger);
        }
        #endregion

        #region ApplicationUpdated Event
        /// <summary>
        /// Occurs when [application updated].
        /// </summary>
        public event EventHandler<GenericEventArgs<Version>> ApplicationUpdated;
        /// <summary>
        /// Called when [application updated].
        /// </summary>
        /// <param name="newVersion">The new version.</param>
        public void OnApplicationUpdated(Version newVersion)
        {
            EventHelper.QueueEventIfNotNull(ApplicationUpdated, this, new GenericEventArgs<Version> { Argument = newVersion }, Logger);

            NotifyPendingRestart();
        }
        #endregion

        /// <summary>
        /// The _configuration loaded
        /// </summary>
        private bool _configurationLoaded;
        /// <summary>
        /// The _configuration sync lock
        /// </summary>
        private object _configurationSyncLock = new object();
        /// <summary>
        /// The _configuration
        /// </summary>
        private TConfigurationType _configuration;
        /// <summary>
        /// Gets the system configuration
        /// </summary>
        /// <value>The configuration.</value>
        public TConfigurationType Configuration
        {
            get
            {
                // Lazy load
                LazyInitializer.EnsureInitialized(ref _configuration, ref _configurationLoaded, ref _configurationSyncLock, () => GetXmlConfiguration<TConfigurationType>(ApplicationPaths.SystemConfigurationFilePath));
                return _configuration;
            }
            protected set
            {
                _configuration = value;

                if (value == null)
                {
                    _configurationLoaded = false;
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance is first run.
        /// </summary>
        /// <value><c>true</c> if this instance is first run; otherwise, <c>false</c>.</value>
        public bool IsFirstRun { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance has changes that require the entire application to restart.
        /// </summary>
        /// <value><c>true</c> if this instance has pending application restart; otherwise, <c>false</c>.</value>
        public bool HasPendingRestart { get; private set; }

        /// <summary>
        /// Gets the application paths.
        /// </summary>
        /// <value>The application paths.</value>
        public TApplicationPathsType ApplicationPaths { get; private set; }

        /// <summary>
        /// Gets the list of currently loaded plugins
        /// </summary>
        /// <value>The plugins.</value>
        public IEnumerable<IPlugin> Plugins { get; protected set; }

        /// <summary>
        /// Gets the web socket listeners.
        /// </summary>
        /// <value>The web socket listeners.</value>
        public IEnumerable<IWebSocketListener> WebSocketListeners { get; private set; }

        /// <summary>
        /// The _HTTP manager
        /// </summary>
        /// <value>The HTTP manager.</value>
        public HttpManager HttpManager { get; private set; }

        /// <summary>
        /// Gets or sets the TCP manager.
        /// </summary>
        /// <value>The TCP manager.</value>
        public TcpManager TcpManager { get; private set; }

        /// <summary>
        /// Gets the rest services.
        /// </summary>
        /// <value>The rest services.</value>
        public IEnumerable<IRestfulService> RestServices { get; private set; }

        /// <summary>
        /// Gets the UDP server port number.
        /// This can't be configurable because then the user would have to configure their client to discover the server.
        /// </summary>
        /// <value>The UDP server port number.</value>
        public abstract int UdpServerPortNumber { get; }

        /// <summary>
        /// Gets the name of the web application that can be used for url building.
        /// All api urls will be of the form {protocol}://{host}:{port}/{appname}/...
        /// </summary>
        /// <value>The name of the web application.</value>
        public string WebApplicationName
        {
            get { return "mediabrowser"; }
        }

        /// <summary>
        /// Gets the HTTP server URL prefix.
        /// </summary>
        /// <value>The HTTP server URL prefix.</value>
        public virtual string HttpServerUrlPrefix
        {
            get
            {
                return "http://+:" + Configuration.HttpServerPortNumber + "/" + WebApplicationName + "/";
            }
        }

        /// <summary>
        /// Gets the kernel context. Subclasses will have to override.
        /// </summary>
        /// <value>The kernel context.</value>
        public abstract KernelContext KernelContext { get; }

        /// <summary>
        /// Gets the log file path.
        /// </summary>
        /// <value>The log file path.</value>
        public string LogFilePath
        {
            get { return ApplicationHost.LogFilePath; }
        }

        /// <summary>
        /// Gets the logger.
        /// </summary>
        /// <value>The logger.</value>
        protected ILogger Logger { get; private set; }

        /// <summary>
        /// Gets or sets the application host.
        /// </summary>
        /// <value>The application host.</value>
        protected IApplicationHost ApplicationHost { get; private set; }

        /// <summary>
        /// The _XML serializer
        /// </summary>
        private readonly IXmlSerializer _xmlSerializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseKernel{TApplicationPathsType}" /> class.
        /// </summary>
        /// <param name="appHost">The app host.</param>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="xmlSerializer">The XML serializer.</param>
        /// <param name="logger">The logger.</param>
        /// <exception cref="System.ArgumentNullException">isoManager</exception>
        protected BaseKernel(IApplicationHost appHost, TApplicationPathsType appPaths, IXmlSerializer xmlSerializer, ILogger logger)
        {
            if (appHost == null)
            {
                throw new ArgumentNullException("appHost");
            }
            if (appPaths == null)
            {
                throw new ArgumentNullException("appPaths");
            }
            if (xmlSerializer == null)
            {
                throw new ArgumentNullException("xmlSerializer");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }

            ApplicationPaths = appPaths;
            ApplicationHost = appHost;
            _xmlSerializer = xmlSerializer;
            Logger = logger;
        }

        /// <summary>
        /// Initializes the Kernel
        /// </summary>
        /// <returns>Task.</returns>
        public Task Init()
        {
            IsFirstRun = !File.Exists(ApplicationPaths.SystemConfigurationFilePath);

            // Performs initializations that can be reloaded at anytime
            return Reload();
        }

        /// <summary>
        /// Performs initializations that can be reloaded at anytime
        /// </summary>
        /// <returns>Task.</returns>
        public async Task Reload()
        {
            OnReloadBeginning();

            await ReloadInternal().ConfigureAwait(false);

            OnReloadCompleted();

            Logger.Info("Kernel.Reload Complete");
        }

        /// <summary>
        /// Performs initializations that can be reloaded at anytime
        /// </summary>
        /// <returns>Task.</returns>
        protected virtual async Task ReloadInternal()
        {
            // Set these to null so that they can be lazy loaded again
            Configuration = null;

            ReloadLogger();

            Logger.Info("Version {0} initializing", ApplicationVersion);

            DisposeHttpManager();
            HttpManager = new HttpManager(this, Logger);

            await OnConfigurationLoaded().ConfigureAwait(false);

            FindParts();

            await OnComposablePartsLoaded().ConfigureAwait(false);

            DisposeTcpManager();
            TcpManager = (TcpManager)ApplicationHost.CreateInstance(typeof(TcpManager));
        }

        /// <summary>
        /// Called when [configuration loaded].
        /// </summary>
        /// <returns>Task.</returns>
        protected virtual Task OnConfigurationLoaded()
        {
            return Task.FromResult<object>(null);
        }

        /// <summary>
        /// Disposes and reloads all loggers
        /// </summary>
        public void ReloadLogger()
        {
            ApplicationHost.ReloadLogger();
            
            OnLoggerLoaded();
        }

        /// <summary>
        /// Composes the parts with ioc container.
        /// </summary>
        protected virtual void FindParts()
        {
            RestServices = ApplicationHost.GetExports<IRestfulService>();
            WebSocketListeners = ApplicationHost.GetExports<IWebSocketListener>();
            Plugins = ApplicationHost.GetExports<IPlugin>();
        }

        /// <summary>
        /// Fires after MEF finishes finding composable parts within plugin assemblies
        /// </summary>
        /// <returns>Task.</returns>
        protected virtual Task OnComposablePartsLoaded()
        {
            return Task.Run(() =>
            {
                // Start-up each plugin
                Parallel.ForEach(Plugins, plugin =>
                {
                    Logger.Info("Initializing {0} {1}", plugin.Name, plugin.Version);

                    try
                    {
                        plugin.Initialize(this, _xmlSerializer, Logger);

                        Logger.Info("{0} {1} initialized.", plugin.Name, plugin.Version);
                    }
                    catch (Exception ex)
                    {
                        Logger.ErrorException("Error initializing {0}", ex, plugin.Name);
                    }
                });
            });
        }

        /// <summary>
        /// Notifies that the kernel that a change has been made that requires a restart
        /// </summary>
        public void NotifyPendingRestart()
        {
            HasPendingRestart = true;

            TcpManager.SendWebSocketMessage("HasPendingRestartChanged", GetSystemInfo());

            EventHelper.QueueEventIfNotNull(HasPendingRestartChanged, this, EventArgs.Empty, Logger);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                DisposeTcpManager();
                DisposeHttpManager();
            }
        }

        /// <summary>
        /// Disposes the TCP manager.
        /// </summary>
        private void DisposeTcpManager()
        {
            if (TcpManager != null)
            {
                TcpManager.Dispose();
                TcpManager = null;
            }
        }

        /// <summary>
        /// Disposes the HTTP manager.
        /// </summary>
        private void DisposeHttpManager()
        {
            if (HttpManager != null)
            {
                HttpManager.Dispose();
                HttpManager = null;
            }
        }

        /// <summary>
        /// Gets the current application version
        /// </summary>
        /// <value>The application version.</value>
        public Version ApplicationVersion
        {
            get
            {
                return GetType().Assembly.GetName().Version;
            }
        }

        /// <summary>
        /// Performs the pending restart.
        /// </summary>
        /// <returns>Task.</returns>
        public void PerformPendingRestart()
        {
            if (HasPendingRestart)
            {
                RestartApplication();
            }
            else
            {
                Logger.Info("PerformPendingRestart - not needed");
            }
        }

        /// <summary>
        /// Restarts the application.
        /// </summary>
        protected void RestartApplication()
        {
            Logger.Info("Restarting the application");

            ApplicationHost.Restart();
        }

        /// <summary>
        /// Gets the system status.
        /// </summary>
        /// <returns>SystemInfo.</returns>
        public virtual SystemInfo GetSystemInfo()
        {
            return new SystemInfo
            {
                HasPendingRestart = HasPendingRestart,
                Version = ApplicationVersion.ToString(),
                IsNetworkDeployed = ApplicationHost.CanSelfUpdate,
                WebSocketPortNumber = TcpManager.WebSocketPortNumber,
                SupportsNativeWebSocket = TcpManager.SupportsNativeWebSocket,
                FailedPluginAssemblies = ApplicationHost.FailedAssemblies.ToArray()
            };
        }

        /// <summary>
        /// The _save lock
        /// </summary>
        private readonly object _configurationSaveLock = new object();

        /// <summary>
        /// Saves the current configuration
        /// </summary>
        public void SaveConfiguration()
        {
            lock (_configurationSaveLock)
            {
                _xmlSerializer.SerializeToFile(Configuration, ApplicationPaths.SystemConfigurationFilePath);
            }

            OnConfigurationUpdated();
        }

        /// <summary>
        /// Gets the application paths.
        /// </summary>
        /// <value>The application paths.</value>
        IApplicationPaths IKernel.ApplicationPaths
        {
            get { return ApplicationPaths; }
        }
        /// <summary>
        /// Gets the configuration.
        /// </summary>
        /// <value>The configuration.</value>
        BaseApplicationConfiguration IKernel.Configuration
        {
            get { return Configuration; }
        }		        
        
        /// <summary>
        /// Reads an xml configuration file from the file system
        /// It will immediately re-serialize and save if new serialization data is available due to property changes
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="path">The path.</param>
        /// <returns>System.Object.</returns>
        public object GetXmlConfiguration(Type type, string path)
        {
            Logger.Info("Loading {0} at {1}", type.Name, path);

            object configuration;

            byte[] buffer = null;

            // Use try/catch to avoid the extra file system lookup using File.Exists
            try
            {
                buffer = File.ReadAllBytes(path);

                configuration = _xmlSerializer.DeserializeFromBytes(type, buffer);
            }
            catch (FileNotFoundException)
            {
                configuration = ApplicationHost.CreateInstance(type);
            }

            // Take the object we just got and serialize it back to bytes
            var newBytes = _xmlSerializer.SerializeToBytes(configuration);

            // If the file didn't exist before, or if something has changed, re-save
            if (buffer == null || !buffer.SequenceEqual(newBytes))
            {
                Logger.Info("Saving {0} to {1}", type.Name, path);

                // Save it after load in case we got new items
                File.WriteAllBytes(path, newBytes);
            }

            return configuration;
        }


        /// <summary>
        /// Reads an xml configuration file from the file system
        /// It will immediately save the configuration after loading it, just
        /// in case there are new serializable properties
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="path">The path.</param>
        /// <returns>``0.</returns>
        private T GetXmlConfiguration<T>(string path)
            where T : class
        {
            return GetXmlConfiguration(typeof(T), path) as T;
        }
    }
}