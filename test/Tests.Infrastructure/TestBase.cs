﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Raven.Client.Http;
using Raven.Server;
using Raven.Server.Config;
using Raven.Server.Config.Settings;
using Raven.Server.Documents;
using Raven.Server.ServerWide;
using Raven.Server.Utils;
using Sparrow.Collections;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Utils;
using Tests.Infrastructure;
using Xunit;

namespace FastTests
{
    public abstract class TestBase : LinuxRaceConditionWorkAround, IDisposable, IAsyncLifetime
    {
        private const string XunitConfigurationFile = "xunit.runner.json";

        private const string ServerName = "Raven.Tests.Core.Server";

        private static readonly ConcurrentSet<string> GlobalPathsToDelete = new ConcurrentSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly SemaphoreSlim ConcurrentTests;

        private readonly ConcurrentSet<string> _localPathsToDelete = new ConcurrentSet<string>(StringComparer.OrdinalIgnoreCase);

        private static RavenServer _globalServer;

        protected static bool IsGlobalServer(RavenServer server)
        {
            return _globalServer == server;
        }

        private RavenServer _localServer;

        protected List<RavenServer> Servers = new List<RavenServer>();

        private static readonly object ServerLocker = new object();

        private bool _doNotReuseServer;

        private IDictionary<string, string> _customServerSettings;

        static TestBase()
        {
#if DEBUG2
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                if (args.Observed)
                    return;

                var e = args.Exception.ExtractSingleInnerException();

                var sb = new StringBuilder();
                sb.AppendLine("===== UNOBSERVED TASK EXCEPTION =====");
                sb.AppendLine(e.ExceptionToString(null));
                sb.AppendLine("=====================================");

                Console.WriteLine(sb.ToString());
            };
#endif

            var maxNumberOfConcurrentTests = Math.Max(ProcessorInfo.ProcessorCount / 2, 2);

            var fileInfo = new FileInfo(XunitConfigurationFile);
            if (fileInfo.Exists)
            {
                using (var file = File.OpenRead(XunitConfigurationFile))
                using (var sr = new StreamReader(file))
                {
                    var json = JObject.Parse(sr.ReadToEnd());
                    if (json.TryGetValue("maxParallelThreads", out JToken token))
                        maxNumberOfConcurrentTests = token.Value<int>();
                }
            }

            Console.WriteLine("Max number of concurrent tests is: " + maxNumberOfConcurrentTests);
            ConcurrentTests = new SemaphoreSlim(maxNumberOfConcurrentTests, maxNumberOfConcurrentTests);
        }

        public void DoNotReuseServer(IDictionary<string, string> customSettings = null)
        {
            _customServerSettings = customSettings;
            _doNotReuseServer = true;
        }

        private static volatile string _selfSignedCertFileName;
        protected static string GenerateAndSaveSelfSignedCertificate()
        {
            if (_selfSignedCertFileName != null)
                return _selfSignedCertFileName;

            lock (typeof(TestBase))
            {
                if (_selfSignedCertFileName != null)
                    return _selfSignedCertFileName;

                var selfCertificate = CertificateUtils.CreateSelfSignedCertificate(Environment.MachineName, "ReplicationBasicTestsSlow");
                RequestExecutor.ServerCertificateCustomValidationCallback += (message, certificate2, arg3, arg4) => true;
                var tempFileName = Path.GetTempFileName();
                byte[] certData = selfCertificate.Export(X509ContentType.Pfx);
                File.WriteAllBytes(tempFileName, certData);
                _selfSignedCertFileName = tempFileName;
                return tempFileName;
            }
        }

        private static int _serverCounter;

        public Task<DocumentDatabase> GetDatabase(string databaseName)
        {
            return Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
        }

        public RavenServer Server
        {
            get
            {
                if (_localServer != null)
                    return _localServer;

                if (_doNotReuseServer)
                {
                    UseNewLocalServer();
                    Servers.Add(_localServer);
                    return _localServer;
                }

                if (_globalServer != null)
                {
                    if (_globalServer.Disposed)
                        throw new ObjectDisposedException("Someone disposed the global server!");
                    _localServer = _globalServer;

                    Servers.Add(_localServer);
                    return _localServer;
                }
                lock (ServerLocker)
                {
                    if (_globalServer == null || _globalServer.Disposed)
                    {
                        var globalServer = GetNewServer();
                        Console.WriteLine($"\tTo attach debugger to test process ({(PlatformDetails.Is32Bits ? "x86" : "x64")}), use proc-id: {Process.GetCurrentProcess().Id}. Url {globalServer.WebUrls[0]}");

                        AssemblyLoadContext.Default.Unloading += UnloadServer;
                        _globalServer = globalServer;
                    }
                    _localServer = _globalServer;
                    Servers.Add(_localServer);
                }
                return _globalServer;
            }
        }

        private void UnloadServer(AssemblyLoadContext obj)
        {
            lock (ServerLocker)
            {
                var copyGlobalServer = _globalServer;
                _globalServer = null;
                if (copyGlobalServer == null)
                    return;
                copyGlobalServer.Dispose();

                GC.Collect(2);
                GC.WaitForPendingFinalizers();

                var exceptionAggregator = new ExceptionAggregator("Failed to cleanup test databases");

                RavenTestHelper.DeletePaths(GlobalPathsToDelete, exceptionAggregator);

                exceptionAggregator.ThrowIfNeeded();
            }
        }

        public void UseNewLocalServer()
        {
            _localServer?.Dispose();
            _localServer = GetNewServer(_customServerSettings);
        }

        private readonly object _getNewServerSync = new object();

        protected RavenServer GetNewServer(IDictionary<string, string> customSettings = null, bool deletePrevious = true, bool runInMemory = true, string partialPath = null)
        {
            lock (_getNewServerSync)
            {
                var configuration = new RavenConfiguration(Guid.NewGuid().ToString(), ResourceType.Server);

                if (customSettings != null)
                {
                    foreach (var setting in customSettings)
                    {
                        configuration.SetSetting(setting.Key, setting.Value);
                    }
                }

                configuration.Initialize();
                configuration.DebugLog.LogMode = LogMode.None;
                if (customSettings == null || customSettings.ContainsKey("Raven/ServerUrl") == false)
                {
                    configuration.Core.ServerUrl = "http://127.0.0.1:0";
                }
                configuration.Server.Name = ServerName;
                configuration.Core.RunInMemory = runInMemory;
                configuration.Core.DataDirectory =
                    configuration.Core.DataDirectory.Combine(partialPath ?? $"Tests{Interlocked.Increment(ref _serverCounter)}");
                configuration.Server.MaxTimeForTaskToWaitForDatabaseToLoad = new TimeSetting(60, TimeUnit.Seconds);
                configuration.Replication.ReplicationMinimalHeartbeat = new TimeSetting(100, TimeUnit.Milliseconds);

                if (deletePrevious)
                    IOExtensions.DeleteDirectory(configuration.Core.DataDirectory.FullPath);

                var server = new RavenServer(configuration);
                server.Initialize();

                return server;
            }
        }

        protected static string UseFiddler(string url)
        {
            if (Debugger.IsAttached && Process.GetProcessesByName("fiddler").Any())
                return url.Replace("127.0.0.1", "localhost.fiddler");

            return url;
        }

        protected static string[] UseFiddler(string[] urls)
        {
            if (!Debugger.IsAttached || !Process.GetProcessesByName("fiddler").Any())
                return urls;

            for (var i = 0; i < urls.Length; i++)
            {
                urls[i] = urls[i].Replace("127.0.0.1", "localhost.fiddler");
            }
            return urls;
        }

        protected static void OpenBrowser(string url)
        {
            Console.WriteLine(url);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"Stop & look at studio\" \"{url}\"")); // Works ok on windows
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url); // Works ok on linux
            }
            else
            {
                Console.WriteLine("Do it yourself!");
            }
        }

        protected string NewDataPath([CallerMemberName] string prefix = null, string suffix = null, bool forceCreateDir = false)
        {
            if (suffix != null)
                prefix += suffix;
            var path = RavenTestHelper.NewDataPath(prefix, _serverCounter, forceCreateDir);

            GlobalPathsToDelete.Add(path);
            _localPathsToDelete.Add(path);

            return path;
        }

        protected abstract void Dispose(ExceptionAggregator exceptionAggregator);

        public virtual void Dispose()
        {
            GC.SuppressFinalize(this);

            if (_initializeAsyncCalled)
                ConcurrentTests.Release();

            var exceptionAggregator = new ExceptionAggregator("Could not dispose test");

            Dispose(exceptionAggregator);

            if (_localServer != null && _localServer != _globalServer)
            {
                exceptionAggregator.Execute(() =>
                {
                    _localServer.Dispose();
                    _localServer = null;
                });
            }

            RavenTestHelper.DeletePaths(_localPathsToDelete, exceptionAggregator);

            exceptionAggregator.ThrowIfNeeded();
        }

        private bool _initializeAsyncCalled;
        public Task InitializeAsync()
        {
            _initializeAsyncCalled = true;
            return ConcurrentTests.WaitAsync();
        }

        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }
    }
}