namespace Boxed.DotnetNewTest
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.TestHost;
    using Xunit;

    /// <summary>
    /// <see cref="Project"/> extension methods.
    /// </summary>
    public static class ProjectExtensions
    {
        private static readonly string[] DefaultUrls = new string[] { "http://localhost", "https://localhost" };

        /// <summary>
        /// Runs 'dotnet restore' on the specified project.
        /// </summary>
        /// <param name="project">The project.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>A task representing the operation.</returns>
        public static Task DotnetRestore(this Project project, TimeSpan? timeout = null) =>
            AssertStartAsync(
                project.DirectoryPath,
                "dotnet",
                "restore",
                CancellationTokenFactory.GetCancellationToken(timeout));

        /// <summary>
        /// Runs 'dotnet build' on the specified project.
        /// </summary>
        /// <param name="project">The project.</param>
        /// <param name="noRestore">Whether to restore the project.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>A task representing the operation.</returns>
        public static Task DotnetBuild(this Project project, bool? noRestore = true, TimeSpan? timeout = null)
        {
            var noRestoreArgument = noRestore == null ? null : "--no-restore";
            return AssertStartAsync(
                project.DirectoryPath,
                "dotnet",
                $"build {noRestoreArgument}",
                CancellationTokenFactory.GetCancellationToken(timeout));
        }

        /// <summary>
        /// Runs 'dotnet publish' on the specified project.
        /// </summary>
        /// <param name="project">The project.</param>
        /// <param name="framework">The framework.</param>
        /// <param name="runtime">The runtime.</param>
        /// <param name="noRestore">Whether to restore the project.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>A task representing the operation.</returns>
        public static Task DotnetPublish(
            this Project project,
            string framework = null,
            string runtime = null,
            bool? noRestore = true,
            TimeSpan? timeout = null)
        {
            var frameworkArgument = framework == null ? null : $"--framework {framework}";
            var runtimeArgument = runtime == null ? null : $"--self-contained --runtime {runtime}";
            var noRestoreArgument = noRestore == null ? null : "--no-restore";
            DirectoryExtensions.CheckCreate(project.PublishDirectoryPath);
            return AssertStartAsync(
                project.DirectoryPath,
                "dotnet",
                $"publish {noRestoreArgument} {frameworkArgument} {runtimeArgument} --output {project.PublishDirectoryPath}",
                CancellationTokenFactory.GetCancellationToken(timeout));
        }

        /// <summary>
        /// Runs 'dotnet run' on the specified project while only exposing a HTTP endpoint.
        /// </summary>
        /// <param name="project">The project.</param>
        /// <param name="projectRelativeDirectoryPath">The project relative directory path.</param>
        /// <param name="action">The action to perform while the project is running.</param>
        /// <param name="noRestore">Whether to restore the project.</param>
        /// <param name="validateCertificate">Validate the project certificate.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>A task representing the operation.</returns>
        public static async Task DotnetRun(
            this Project project,
            string projectRelativeDirectoryPath,
            Func<HttpClient, Task> action,
            bool? noRestore = true,
            Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> validateCertificate = null,
            TimeSpan? timeout = null)
        {
            var httpPort = PortHelper.GetFreeTcpPort();
            var httpUrl = $"http://localhost:{httpPort}";

            var projectFilePath = Path.Combine(project.DirectoryPath, projectRelativeDirectoryPath);
            var dotnetRun = await DotnetRunInternal(projectFilePath, noRestore, timeout, httpUrl);

            var httpClientHandler = new HttpClientHandler()
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = validateCertificate ?? DefaultValidateCertificate,
            };
            var httpClient = new HttpClient(httpClientHandler) { BaseAddress = new Uri(httpUrl) };

            Exception unhandledException = null;
            try
            {
                await action(httpClient);
            }
            catch (Exception exception)
            {
                unhandledException = exception;
            }

            httpClient.Dispose();
            httpClientHandler.Dispose();
            dotnetRun.Dispose();

            if (unhandledException != null)
            {
                Assert.False(true, unhandledException.ToString());
            }
        }

        /// <summary>
        /// Runs 'dotnet run' on the specified project while only exposing a HTTP and HTTPS endpoint.
        /// </summary>
        /// <param name="project">The project.</param>
        /// <param name="projectRelativeDirectoryPath">The project relative directory path.</param>
        /// <param name="action">The action to perform while the project is running.</param>
        /// <param name="noRestore">Whether to restore the project.</param>
        /// <param name="validateCertificate">Validate the project certificate.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>A task representing the operation.</returns>
        public static async Task DotnetRun(
            this Project project,
            string projectRelativeDirectoryPath,
            Func<HttpClient, HttpClient, Task> action,
            bool? noRestore = true,
            Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> validateCertificate = null,
            TimeSpan? timeout = null)
        {
            var httpPort = PortHelper.GetFreeTcpPort();
            var httpsPort = PortHelper.GetFreeTcpPort();
            var httpUrl = $"http://localhost:{httpPort}";
            var httpsUrl = $"https://localhost:{httpsPort}";

            var projectFilePath = Path.Combine(project.DirectoryPath, projectRelativeDirectoryPath);
            var dotnetRun = await DotnetRunInternal(projectFilePath, noRestore, timeout, httpUrl, httpsUrl);

            var httpClientHandler = new HttpClientHandler()
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = validateCertificate ?? DefaultValidateCertificate,
            };
            var httpClient = new HttpClient(httpClientHandler) { BaseAddress = new Uri(httpUrl) };
            var httpsClient = new HttpClient(httpClientHandler) { BaseAddress = new Uri(httpsUrl) };

            Exception unhandledException = null;
            try
            {
                await action(httpClient, httpsClient);
            }
            catch (Exception exception)
            {
                unhandledException = exception;
            }

            httpClient.Dispose();
            httpsClient.Dispose();
            httpClientHandler.Dispose();
            dotnetRun.Dispose();

            if (unhandledException != null)
            {
                Assert.False(true, unhandledException.ToString());
            }
        }

        /// <summary>
        /// Runs the project in-memory.
        /// </summary>
        /// <param name="project">The project.</param>
        /// <param name="action">The action to perform while the project is running.</param>
        /// <param name="environmentName">Name of the environment.</param>
        /// <param name="startupTypeName">Name of the startup type.</param>
        /// <returns>A task representing the operation.</returns>
        /// <remarks>This doesn't work yet, needs API's from .NET Core 3.0.</remarks>
        internal static async Task DotnetRunInMemory(
            this Project project,
            Func<TestServer, Task> action,
            string environmentName = "Development",
            string startupTypeName = "Startup")
        {
            var projectName = Path.GetFileName(project.DirectoryPath);
            var directoryPath = project.PublishDirectoryPath;
            var assemblyFilePath = Path.Combine(directoryPath, $"{projectName}.dll");

            if (string.IsNullOrEmpty(assemblyFilePath))
            {
                Assert.False(true, $"Project assembly {assemblyFilePath} not found.");
            }
            else
            {
                var assembly = new AssemblyResolver(assemblyFilePath).Assembly;
                var startupType = assembly
                    .DefinedTypes
                    .FirstOrDefault(x => string.Equals(x.Name, startupTypeName, StringComparison.Ordinal));
                if (startupType == null)
                {
                    Assert.False(true, $"Startup type '{startupTypeName}' not found.");
                }

                var webHostBuilder = new WebHostBuilder()
                    .UseEnvironment(environmentName)
                    .UseStartup(startupType)
                    .UseUrls(DefaultUrls);
                using (var testServer = new TestServer(webHostBuilder))
                {
                    await action(testServer);
                }

                // TODO: Unload startupType when supported: https://github.com/dotnet/corefx/issues/14724
            }
        }

        private static async Task<IDisposable> DotnetRunInternal(
            string directoryPath,
            bool? noRestore = true,
            TimeSpan? timeout = null,
            params string[] urls)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var noRestoreArgument = noRestore == null ? null : "--no-restore";
            var urlsParameter = string.Join(";", urls);
            var task = AssertStartAsync(
                directoryPath,
                "dotnet",
                $"run {noRestoreArgument} --urls {urlsParameter}",
                cancellationTokenSource.Token);
            await WaitForStart(urls.First(), timeout ?? TimeSpan.FromMinutes(1));

            return new DisposableAction(
                () =>
                {
                    cancellationTokenSource.Cancel();

                    try
                    {
                        task.Wait();
                    }
                    catch (AggregateException exception)
                    when (exception.GetBaseException().GetBaseException() is TaskCanceledException)
                    {
                    }
                });
        }

        private static async Task WaitForStart(string url, TimeSpan timeout)
        {
            const int intervalMilliseconds = 100;

            var httpClientHandler = new HttpClientHandler()
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = DefaultValidateCertificate,
            };
            using (var httpClient = new HttpClient(httpClientHandler) { BaseAddress = new Uri(url) })
            {
                for (var i = 0; i < (timeout.TotalMilliseconds / intervalMilliseconds); ++i)
                {
                    try
                    {
                        _ = await httpClient.GetAsync("/");
                        return;
                    }
                    catch (HttpRequestException exception)
                    when (IsApiDownException(exception))
                    {
                        await Task.Delay(intervalMilliseconds);
                    }
                }

                throw new TimeoutException(
                    $"Timed out after waiting {timeout} for application to start using dotnet run.");
            }
        }

        private static bool IsApiDownException(Exception exception)
        {
            var result = false;
            var baseException = exception.GetBaseException();
            if (baseException is SocketException socketException)
            {
                result =
                    string.Equals(
                        socketException.Message,
                        "No connection could be made because the target machine actively refused it",
                        StringComparison.Ordinal)
                    ||
                    string.Equals(
                        socketException.Message,
                        "Connection refused",
                        StringComparison.Ordinal);
            }

            return result;
        }

        private static bool DefaultValidateCertificate(
            HttpRequestMessage request,
            X509Certificate2 certificate,
            X509Chain chain,
            SslPolicyErrors errors) => true;

        private static async Task AssertStartAsync(
            string workingDirectory,
            string fileName,
            string arguments,
            CancellationToken cancellationToken)
        {
            var (processResult, message) = await ProcessExtensions.StartAsync(
                workingDirectory,
                fileName,
                arguments,
                cancellationToken);
            if (processResult != ProcessResult.Succeeded)
            {
                Assert.False(true, message);
            }
        }
    }
}
