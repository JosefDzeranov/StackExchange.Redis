﻿using StackExchange.Redis.Tests.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace StackExchange.Redis.Tests.Booksleeve
{
    public class BookSleeveTestBase
    {
        public ITestOutputHelper Output { get; }

        public BookSleeveTestBase(ITestOutputHelper output)
        {
            Output = output;
            Output.WriteFrameworkVersion();
        }

        static BookSleeveTestBase()
        {
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Trace.WriteLine(args.Exception, "UnobservedTaskException");
                args.SetObserved();
            };
        }

        protected static string Me([CallerFilePath] string filePath = null, [CallerMemberName] string caller = null) => TestBase.Me(filePath, caller);

        internal static IServer GetServer(ConnectionMultiplexer conn) => conn.GetServer(conn.GetEndPoints()[0]);

        internal static ConnectionMultiplexer GetRemoteConnection(bool open = true, bool allowAdmin = false, bool waitForOpen = false, int syncTimeout = 5000, int ioTimeout = 5000)
        {
            return GetConnection(TestConfig.Current.RemoteServer, TestConfig.Current.RemotePort, open, allowAdmin, waitForOpen, syncTimeout, ioTimeout);
        }

        private static ConnectionMultiplexer GetConnection(string host, int port, bool open = true, bool allowAdmin = false, bool waitForOpen = false, int syncTimeout = 5000, int ioTimeout = 5000)
        {
            var options = new ConfigurationOptions
            {
                EndPoints = { { host, port } },
                AllowAdmin = allowAdmin,
                SyncTimeout = syncTimeout,
                ResponseTimeout = ioTimeout
            };
            var conn = ConnectionMultiplexer.Connect(options);
            conn.InternalError += (s, args) => Trace.WriteLine(args.Exception.Message, args.Origin);
            if (open && waitForOpen)
            {
                conn.GetDatabase().Ping();
            }
            return conn;
        }

        internal static ConnectionMultiplexer GetUnsecuredConnection(bool open = true, bool allowAdmin = false, bool waitForOpen = false, int syncTimeout = 5000, int ioTimeout = 5000)
        {
            return GetConnection(TestConfig.Current.MasterServer, TestConfig.Current.MasterPort, open, allowAdmin, waitForOpen, syncTimeout, ioTimeout);
        }

        internal static ConnectionMultiplexer GetSecuredConnection()
        {
            Skip.IfNoConfig(nameof(TestConfig.Config.SecureServer), TestConfig.Current.SecureServer);

            var options = new ConfigurationOptions
            {
                EndPoints = { { TestConfig.Current.SecureServer, TestConfig.Current.SecurePort } },
                Password = "changeme",
                SyncTimeout = 6000,
            };
            var conn = ConnectionMultiplexer.Connect(options);
            conn.InternalError += (s, args) => Trace.WriteLine(args.Exception.Message, args.Origin);
            return conn;
        }
    }
}
