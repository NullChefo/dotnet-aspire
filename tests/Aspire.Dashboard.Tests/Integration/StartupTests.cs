// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Xunit;
using Xunit.Abstractions;

namespace Aspire.Dashboard.Tests.Integration;

public class StartupTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task EndPointAccessors_AppStarted_EndPointPortsAssigned()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);

        // Act
        await app.StartAsync();

        // Assert
        AssertDynamicIPEndpoint(app.FrontendEndPointAccessor);
        AssertDynamicIPEndpoint(app.OtlpServiceEndPointAccessor);
    }

    [Fact]
    public async Task Configuration_NoExtraConfig_Error()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data.Clear();
            });

        // Assert
        Assert.Collection(app.ValidationFailures,
            s => s.Contains("Dashboard:Frontend:EndpointUrls"),
            s => s.Contains("Dashboard:Frontend:AuthMode"),
            s => s.Contains("Dashboard:Otlp:EndpointUrl"),
            s => s.Contains("Dashboard:Otlp:AuthMode"));
    }

    [Fact]
    public async Task Configuration_ConfigFilePathDoesntExist_Error()
    {
        // Arrange & Act
        var configFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                additionalConfiguration: data =>
                {
                    data[DashboardConfigNames.DashboardConfigFilePathName.ConfigKey] = configFilePath;
                });
        });

        // Assert
        Assert.Contains(configFilePath, ex.Message);
    }

    [Fact]
    public async Task Configuration_OptionsMonitor_CanReadConfiguration()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: initialData =>
            {
                initialData["Dashboard:Otlp:AuthMode"] = nameof(OtlpAuthMode.ApiKey);
                initialData["Dashboard:Otlp:PrimaryApiKey"] = "TestKey123!";
            });

        // Act
        await app.StartAsync();

        // Assert
        Assert.Equal(OtlpAuthMode.ApiKey, app.DashboardOptionsMonitor.CurrentValue.Otlp.AuthMode);
        Assert.Equal("TestKey123!", app.DashboardOptionsMonitor.CurrentValue.Otlp.PrimaryApiKey);
    }

    [Fact]
    public async Task Configuration_BrowserAndOtlpEndpointSame_Https_EndPointPortsAssigned()
    {
        // Arrange
        DashboardWebApplication? app = null;
        try
        {
            await ServerRetryHelper.BindPortsWithRetry(async port =>
            {
                app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                    additionalConfiguration: initialData =>
                    {
                        initialData[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = $"https://127.0.0.1:{port}";
                        initialData[DashboardConfigNames.DashboardOtlpUrlName.ConfigKey] = $"https://127.0.0.1:{port}";
                    });

                // Act
                await app.StartAsync();
            }, NullLogger.Instance);

            // Assert
            Assert.NotNull(app);
            Assert.Equal(app.FrontendEndPointAccessor().EndPoint.Port, app.OtlpServiceEndPointAccessor().EndPoint.Port);

            // Check browser access
            using var httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    return true;
                }
            })
            {
                BaseAddress = new Uri($"https://{app.FrontendEndPointAccessor().EndPoint}")
            };
            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // Check OTLP service
            using var channel = IntegrationTestHelpers.CreateGrpcChannel($"https://{app.FrontendEndPointAccessor().EndPoint}", testOutputHelper);
            var client = new LogsService.LogsServiceClient(channel);
            var serviceResponse = await client.ExportAsync(new ExportLogsServiceRequest());
            Assert.Equal(0, serviceResponse.PartialSuccess.RejectedLogRecords);
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Configuration_BrowserAndOtlpEndpointSame_Https_Error()
    {
        // Arrange
        DashboardWebApplication? app = null;
        var testSink = new TestSink();
        try
        {
            await ServerRetryHelper.BindPortsWithRetry(async port =>
            {
                app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                    additionalConfiguration: initialData =>
                    {
                        initialData[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = $"http://127.0.0.1:{port}";
                        initialData[DashboardConfigNames.DashboardOtlpUrlName.ConfigKey] = $"http://127.0.0.1:{port}";
                    },
                    testSink: testSink);

                // Act
                await app.StartAsync();
            }, NullLogger.Instance);

            // Assert
            Assert.Contains(testSink.Writes, w =>
            {
                if (w.LoggerName != typeof(DashboardWebApplication).FullName)
                {
                    return false;
                }
                if (w.LogLevel != LogLevel.Warning)
                {
                    return false;
                }
                if (!w.Message?.Contains("The dashboard is configured with a shared endpoint for browser access and the OTLP service. The endpoint doesn't use TLS so browser access is only possible via a TLS terminating proxy.") ?? false)
                {
                    return false;
                }
                return true;
            });
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Configuration_NoOtlpAuthMode_Error()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data.Remove(DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey);
            });

        // Assert
        Assert.Contains("Dashboard:Otlp:AuthMode", app.ValidationFailures.Single());
    }

    [Fact]
    public async Task Configuration_AllowAnonymous_NoError()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data.Remove(DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey);
                data[DashboardConfigNames.DashboardUnsecuredAllowAnonymousName.ConfigKey] = bool.TrueString;
            });

        // Act
        await app.StartAsync();

        // Assert
        AssertDynamicIPEndpoint(app.FrontendEndPointAccessor);
        AssertDynamicIPEndpoint(app.OtlpServiceEndPointAccessor);
    }

    [Fact]
    public async Task LogOutput_DynamicPort_PortResolvedInLogs()
    {
        // Arrange
        var testSink = new TestSink();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, testSink: testSink);

        // Act
        await app.StartAsync();

        // Assert
        var l = testSink.Writes.Where(w => w.LoggerName == typeof(DashboardWebApplication).FullName).ToList();
        Assert.Collection(l,
            w =>
            {
                Assert.Equal("Aspire version: {Version}", GetValue(w.State, "{OriginalFormat}"));
            },
            w =>
            {
                Assert.Equal("Now listening on: {DashboardUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "DashboardUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP server running at: {OtlpEndpointUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "OtlpEndpointUri")!);
                Assert.NotEqual(0, uri.Port);
            });

        object? GetValue(object? values, string key)
        {
            var list = values as IReadOnlyList<KeyValuePair<string, object>>;
            return list?.SingleOrDefault(kvp => kvp.Key == key).Value;
        }
    }

    [Fact]
    public async Task EndPointAccessors_AppStarted_BrowserGet_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);

        // Act
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.GetAsync("/");

        // Assert
        response.EnsureSuccessStatusCode();
    }

    private static void AssertDynamicIPEndpoint(Func<EndpointInfo> endPointAccessor)
    {
        // Check that the specified dynamic port of 0 is overridden with the actual port number.
        var ipEndPoint = endPointAccessor().EndPoint;
        Assert.NotEqual(0, ipEndPoint.Port);
    }
}
