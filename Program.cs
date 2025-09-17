using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Reflection;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Infozahyst.RSAAS.Common;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Messaging;
using Infozahyst.RSAAS.Common.Settings;
using Infozahyst.RSAAS.Common.Streaming;
using Infozahyst.RSAAS.Common.Streaming.Interfaces;
using Infozahyst.RSAAS.Common.Transport;
using Infozahyst.RSAAS.Core.Tools;
using Infozahyst.RSAAS.Core.Transport.DataStreaming;
using Infozahyst.RSAAS.Core.Transport.DataStreaming.Data.Enums;
using Infozahyst.RSAAS.Core.Transport.DataStreaming.Listeners;
using Infozahyst.RSAAS.Core.Transport.MQTT;
using Infozahyst.RSAAS.Core.Transport.MQTT.Interfaces;
using Infozahyst.RSAAS.Server.AntennaRotator;
using Infozahyst.RSAAS.Server.AntennaRotator.Kolovrat;
using Infozahyst.RSAAS.Server.AntennaRotator.RotorRAK;
using Infozahyst.RSAAS.Server.Controllers;
using Infozahyst.RSAAS.Server.Controllers.Interfaces;
using Infozahyst.RSAAS.Server.DAL;
using Infozahyst.RSAAS.Server.DataStream;
using Infozahyst.RSAAS.Server.Geolocation.Aoa.AoaSingle;
using Infozahyst.RSAAS.Server.Geolocation.Aoa.AoaSingle.Interfaces;
using Infozahyst.RSAAS.Server.Geolocation.Tdoa.Ptoa;
using Infozahyst.RSAAS.Server.Geolocation.Tdoa.Ptoa.Interfaces;
using Infozahyst.RSAAS.Server.HostServices;
using Infozahyst.RSAAS.Server.Models;
using Infozahyst.RSAAS.Server.Models.Pdw;
using Infozahyst.RSAAS.Server.Positioner;
using Infozahyst.RSAAS.Server.Receiver;
using Infozahyst.RSAAS.Server.Receiver.IQ;
using Infozahyst.RSAAS.Server.Receiver.NetSdr;
using Infozahyst.RSAAS.Server.Receiver.Pdw;
using Infozahyst.RSAAS.Server.Receiver.PersistenceSpectrum;
using Infozahyst.RSAAS.Server.Receiver.Spectrum;
using Infozahyst.RSAAS.Server.Receiver.StreamRecording.IQ;
using Infozahyst.RSAAS.Server.Receiver.StreamRecording.Pdw;
using Infozahyst.RSAAS.Server.Receiver.Supervisor;
using Infozahyst.RSAAS.Server.Services;
using Infozahyst.RSAAS.Server.Services.ClusterizationFlow;
using Infozahyst.RSAAS.Server.Services.GnssRecordingService;
using Infozahyst.RSAAS.Server.Services.Interfaces;
using Infozahyst.RSAAS.Server.Services.StreamingServices;
using Infozahyst.RSAAS.Server.Services.StreamingServices.Interfaces;
using Infozahyst.RSAAS.Server.Settings;
using Infozahyst.RSAAS.Server.Settings.Geolocation;
using Infozahyst.RSAAS.Server.Settings.Providers.Db;
using Infozahyst.RSAAS.Server.Teneta;
using Infozahyst.RSAAS.Server.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using Microsoft.Win32;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Layouts;
using NLog.Targets;
using QuicListener = System.Net.Quic.QuicListener;

namespace Infozahyst.RSAAS.Server;

class Program
{
    private static string _appName = null!;
    private static IHost? _host;
    private static ILogger<Program>? _logger;
    private static IMessageLogger? _messageLogger;
    private static readonly TaskCompletionSource TaskCompletionSource = new();

    private static bool ConsoleSignalHandler(NativeUtils.CtrlType sig) {
        try {
            _logger?.LogInformation("Cleanup started");
            _host?.StopAsync().GetAwaiter().GetResult();

            SqliteConnection.ClearAllPools();
            _logger?.LogInformation("Cleanup ended");
        } finally {
            LogManager.Shutdown();
            TaskCompletionSource.SetResult();
        }

        return true;
    }

    public static async Task<int> Main(string[] args) {
        LogManager.AutoShutdown = false;

        var version = GetVersion();
        _appName = Assembly.GetExecutingAssembly().GetName().Name ?? "Infozahyst.RSAAS.Server";

        if (Environment.UserInteractive) {
            Console.Title = $"Infozahyst.RSAAS.Server v{version}";
            RegisterConsoleSignalHandler();
        }

        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Mutex? mutex = null;
        try {
            Directory.SetCurrentDirectory(new DirectoryInfo(Assembly.GetEntryAssembly()!.Location).Parent!.FullName);
            SetWorkDirectories();
            SetCulture();

            var builder = new HostBuilder();
            _host = builder
                .ConfigureAppConfiguration((hostBuilderContext, configurationBuilder) =>
                    ConfigureAppConfiguration(configurationBuilder, hostBuilderContext))
                .ConfigureServices((hostBuilderContext, serviceCollection) =>
                    ConfigureServices(serviceCollection, hostBuilderContext))
                .UseConsoleLifetime()
                .UseWindowsService()
                .Build();
            _logger = _host.Services.GetRequiredService<ILogger<Program>>();

            SetupSettings();

            var configuration = _host.Services.GetRequiredService<IConfiguration>();
            var stationId = configuration.GetValue<uint>("General:StationId");
            string mutexId = $"Global\\{typeof(Program).FullName}-{stationId}";
            mutex = new Mutex(false, mutexId, out bool created);
            if (created == false) {
                throw new Exception("Another instance of application is running");
            }

            _messageLogger = _host.Services.GetRequiredService<IMessageLogger>();

            _logger.LogInformation("{ProgramName} version:{Version}", typeof(Program).FullName, version);
            ApplyMigrations();

            if (Environment.UserInteractive) {
                await _host.StartAsync();
                // change behavior of IHost.WaitForShutdownAsync to custom handling in ConsoleSignalHandler()
                // this logic allow run IHostedService.StopAsync once on exit from console
                await TaskCompletionSource.Task;
            } else {
                await _host.RunAsync();
            }
        } catch (InvalidOperationException e)
            when (e is { InnerException: ArgumentException, Source: "Microsoft.Extensions.Configuration.Binder" }) {
            _logger?.LogError("{Message}-{InnerMessage}", e.Message, e.InnerException?.Message);
        } catch (Exception ex) {
            _logger?.LogError(ex, "An error occurred on startup");
            if (Debugger.IsAttached) {
                Debugger.Break();
            }

            if (_logger == null) {
                LogManager.Configuration ??= CreateFallbackLoggingConfiguration();
                var logger = LogManager.GetCurrentClassLogger();
                logger.Log(NLog.LogLevel.Error, ex, "An error occurred on startup");
                Console.SetError(new StringWriter(new StringBuilder(ex.ToString())));
            }

            return -1;
        } finally {
            mutex?.Dispose();
        }

        return 0;
    }

    private static LoggingConfiguration CreateFallbackLoggingConfiguration() {
        var config = new LoggingConfiguration();
        var fallbackFile = new FileTarget("fallback") {
            FileName = $"Logs/{DateTime.UtcNow:yyyy-MM-dd}_fallback.json",
            Layout = new JsonLayout {
                Attributes = {
                    new JsonAttribute("Timestamp", "${date:format=o}"),
                    new JsonAttribute("Level", "${level}"),
                    new JsonAttribute("Logger", "${logger}"),
                    new JsonAttribute("Message", "${message}"),
                    new JsonAttribute("Exception",
                        "${exception:format=Message,StackTrace,Data:maxInnerExceptionLevel=5}")
                }
            }
        };

        config.AddTarget(fallbackFile);
        config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, fallbackFile);

        return config;
    }

    private static void RegisterConsoleSignalHandler() {
        NativeUtils.RegisterConsoleSignalHandler(ConsoleSignalHandler);
    }

    private static void SetupSettings() {
        var receiverSettings = _host!.Services.GetRequiredService<IOptions<ReceiverSettings>>();
        if (string.IsNullOrWhiteSpace(receiverSettings.Value.DatastreamIpAddress)) {
            var dataStreamInterface = GetDataStreamInterface();
            if (dataStreamInterface != null) {
                var ipAddress = dataStreamInterface.GetIpAddress();
                receiverSettings.Value.DatastreamIpAddress = ipAddress.ToString();

                _logger!.LogInformation("Chosen data-stream interface: {Interface} with ip {IpAddress}",
                    dataStreamInterface.Name, ipAddress);
            } else {
                if (string.IsNullOrWhiteSpace(receiverSettings.Value.DatastreamIpAddress)) {
                    receiverSettings.Value.DatastreamIpAddress = "127.0.0.1";
                }
            }
        }

        var streamingSettings = _host.Services.GetRequiredService<IOptions<StreamingSettings>>();
        if (string.IsNullOrEmpty(streamingSettings.Value.RepeaterLocalIpAddress)) {
            streamingSettings.Value.RepeaterLocalIpAddress = null;
        }

        if (!QuicConnection.IsSupported || !QuicListener.IsSupported) {
            var keys = streamingSettings.Value.Protocols
                .Where(a => a.Value == DataProtocol.Quic)
                .Select(a => a.Key);
            foreach (var key in keys) {
                streamingSettings.Value.Protocols[key] = DataProtocol.Udp;
                _logger!.LogWarning(
                    "Change StreamingSettings.Protocols for {Stream} QUIC->UDP because QUIC not supported", key);
            }
        }

        if (!AdminPrivilegeHelper.IsRunningAsAdministrator()) {
            _logger!.LogWarning(
                "Server is not running with administrator privileges. Registry modifications will be skipped.");
        } else {
            RegistryHelper.ModifyParams("SYSTEM\\CurrentControlSet\\Services\\MsQuic\\Parameters",
                new Dictionary<string, (object Value, RegistryValueKind Type)> {
                    { "InitialRttMs", (streamingSettings.Value.Quic.InitialRttMs, RegistryValueKind.DWord) },
                    { "MaxAckDelayMs", (streamingSettings.Value.Quic.MaxAckDelayMs, RegistryValueKind.DWord) }, {
                        "MaxWorkerQueueDelayMs",
                        (streamingSettings.Value.Quic.MaxWorkerQueueDelayMs, RegistryValueKind.DWord)
                    }, {
                        "MtuDiscoveryMissingProbeCount",
                        (streamingSettings.Value.Quic.MtuDiscoveryMissingProbeCount, RegistryValueKind.DWord)
                    }, {
                        "MtuDiscoverySearchCompleteTimeoutUs",
                        (streamingSettings.Value.Quic.MtuDiscoverySearchCompleteTimeoutUs, RegistryValueKind.QWord)
                    }, {
                        "StreamRecvBufferDefault",
                        (streamingSettings.Value.Quic.StreamRecvBufferDefault, RegistryValueKind.DWord)
                    }, {
                        "StreamRecvWindowDefault",
                        (streamingSettings.Value.Quic.StreamRecvBufferDefault, RegistryValueKind.DWord)
                    }, {
                        "StreamRecvWindowUnidiDefault",
                        (streamingSettings.Value.Quic.StreamRecvBufferDefault, RegistryValueKind.DWord)
                    },
                    { "MinimumMtu", (streamingSettings.Value.Quic.MinimumMtu, RegistryValueKind.DWord) }
                });
        }
    }

    private static NetworkInterface? GetDataStreamInterface() {
        var allInterfaces = NetworkUtils.GetNetworkInterfaces();

        if (allInterfaces.Length == 0) {
            _logger?.LogWarning("No network interfaces was found");
            return null;
        }

        var highestDataStreamInterfaces = allInterfaces
            .GroupBy(a => a.Speed)
            .MaxBy(a => a.Key);
        if (highestDataStreamInterfaces?.Count() > 1) {
            _logger?.LogInformation(
                "More than one high speed interfaces exist (none of them will be used) {@Interfaces}",
                highestDataStreamInterfaces.Select(a => new {
                    a.Id,
                    a.Name,
                    Mac = a.GetPhysicalAddress(),
                    Ip = a.GetIpAddress()?.ToString(),
                    a.Speed
                }));
            return null;
        }

        return allInterfaces.MaxBy(a => a.Speed);
    }

    private static void ConfigureAppConfiguration(IConfigurationBuilder configurationBuilder,
        HostBuilderContext hostBuilderContext) {
        var configFileName = $"{_appName}.settings.json";
        var prodConfigFileName = $"{_appName}.settings.prod.json";

        configurationBuilder
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(configFileName)
            .AddJsonFile(prodConfigFileName, true)
            .AddEnvironmentVariables();

        var portableConfiguration = configurationBuilder.Build();

        var appDataPathSettings = portableConfiguration.GetSection(AppDataPathSettings.Key).Get<AppDataPathSettings>();
        if (appDataPathSettings != null && !string.IsNullOrEmpty(appDataPathSettings.WorkingFilePath)) {
            var dbPath = configurationBuilder.Build().GetConnectionString("db")!;
            dbPath = AppDataPathService.ReplaceAppDataPathVariablesWithPortablePath(dbPath,
                appDataPathSettings.WorkingFilePath ?? "./");
            configurationBuilder
                .AddEnvironmentVariables()
                .AddDbConfiguration(dbPath, hostBuilderContext)
                .Build();

            return;
        }

        var commonConfigFilePath = Path.Combine(AppDataPathService.GetCommonAppDataPath(), configFileName);
        var userConfigFilePath = Path.Combine(AppDataPathService.GetUserAppDataPath(), configFileName);

        configurationBuilder
            .AddJsonFile(commonConfigFilePath, true)
            .AddJsonFile(userConfigFilePath, true)
            .AddEnvironmentVariables()
            .AddDbConfiguration(configurationBuilder.Build().GetConnectionString("db")!, hostBuilderContext)
            .Build();
    }

    private const string GeneralSettingsKey = "General";
    private const string AntennaRotatorSettingsKey = "AntennaRotator";
    private const string SupervisorSettingsKey = "Supervisor";
    private const string PositionerSettingsKey = "Positioner";
    private const string ClusterizationSettingsKey = "Clusterization";

    private static void ConfigureServices(IServiceCollection services, HostBuilderContext hostBuilderContext) {
        var configuration = hostBuilderContext.Configuration;
        services.AddDbConfiguration(hostBuilderContext);
        services.AddFeatureManagement(configuration);

        var appDataPathService = SetupAppDataPathService(services, configuration);

        services.AddLogging(builder => {
            var loggingConfiguration = new NLogLoggingConfiguration(configuration.GetSection("NLog"));
            loggingConfiguration.Variables.Add(AppDataPathService.CommonAppDataPathProperty,
                appDataPathService.GetCommonAppDataPathOrPortablePath());
            var stationId = configuration.GetValue<string>("General:StationId");
            if (string.IsNullOrWhiteSpace(stationId)) {
                throw new ArgumentException("", nameof(stationId));
            }

            loggingConfiguration.Variables.Add("%station-id%", Layout.FromString(stationId));
            builder.ClearProviders();
            builder.AddNLog(loggingConfiguration);
        });
        services.AddTransient<TraceListener, NLogTraceListener>();
        var policyRegistryConfigSection = configuration.GetSection("PolicyRegistry");
        var policyRegistrySettings = policyRegistryConfigSection.Get<PolicyRegistrySettings>();
        services.AddPollyPolicyRegistry(policyRegistrySettings ?? new PolicyRegistrySettings());

        services.RegisterRsaasDatabase(configuration, appDataPathService);
        services.Configure<ReceiverSettings>(configuration.GetSection("Receiver"));
        services.Configure<GeneralSettings>(configuration.GetSection(GeneralSettingsKey));
        services.Configure<SupervisorSettings>(configuration.GetSection(SupervisorSettingsKey));
        services.Configure<PositionerSettings>(configuration.GetSection(PositionerSettingsKey));
        services.Configure<AntennaRotatorSettings>(configuration.GetSection(AntennaRotatorSettingsKey));
        services.Configure<RadarAssociationSettings>(configuration.GetSection("RadarAssociation"));
        services.Configure<TenetaSettings>(configuration.GetSection("Teneta"));
        services.Configure<ClusterizationSettings>(configuration.GetSection(ClusterizationSettingsKey));
        services.Configure<DeinterleavingSettings>(configuration.GetSection("Deinterleaving"));
        services.Configure<MqttSettings>(configuration.GetSection("Mqtt"));
        services.Configure<StreamingSettings>(configuration.GetSection("Streaming"));
        services.Configure<ServerSettings>(configuration.GetSection("Server"));
        services.AddOptions<TdoaSettings>().Bind(configuration.GetSection("Geolocation:Tdoa"));
        services.AddOptions<AoaSettings>().Bind(configuration.GetSection("Geolocation:Aoa"));
        services.Configure<ClusterGroupingSettings>(configuration.GetSection("ClusterGrouping"));
        services.AddOptions<PdwClusterInitParams>().Bind(configuration.GetSection("Clusterization:Parallel"))
            .Validate(
                x => {
                    var clusterizationSettings =
                        configuration.GetSection("Clusterization").Get<ClusterizationSettings>();
                    if (clusterizationSettings != null && x.PdwBufferSize <= clusterizationSettings.PdwMaxLimit) {
                        return true;
                    }

                    _logger?.LogError("PdwBufferSize must be less than PdwMaxLimit");
                    return false;
                }).ValidateOnStart();
        services.Configure<MapSettings>(configuration.GetSection("Map"));
        services.AddSingleton<ISettingsStorage, DbSettingsStorage>();
        services.Configure<HostOptions>(opts => {
            opts.ShutdownTimeout = TimeSpan.FromSeconds(30);
            opts.ServicesStopConcurrently = false;
        });

        services.AddSingleton<PdwClient>();
        services.AddTransient<IFilePdwClient, FilePdwClient>();
        services.AddSingleton<IPdwClient>(provider => provider.GetRequiredService<PdwClient>());
        services.AddSingleton<IPersistenceSpectrumClient, PersistenceSpectrumClient>();
        var stationType = configuration.GetValue<StationType>("General:StationType");
        if (stationType == StationType.GekataGA) {
            services.AddSingleton<IControlClient, MinervaGAControlClient>();
        } else {
            services.AddSingleton<IControlClient, MinervaControlClient>();
        }

        services.AddSingleton<IIQClient, IQClient>();
        services.AddSingleton<ISupervisorClient, SupervisorClient>();
        services.AddSingleton<IPositionerClient, InfoVectorClient>();
        services.AddSingleton<ISpectrumClient, SpectrumClient>();
        services.AddSingleton<IThresholdClient, ThresholdClient>();

        var antennaRotatorSettings =
            configuration.GetSection(AntennaRotatorSettingsKey).Get<AntennaRotatorSettings>();
        var generalSettings = configuration.GetSection(GeneralSettingsKey).Get<GeneralSettings>();
        if (!string.IsNullOrWhiteSpace(antennaRotatorSettings?.IpAddress) &&
            generalSettings?.StationType != StationType.ArchontT) {
            switch (antennaRotatorSettings.Type) {
                case AntennaRotatorType.AltAzimuth:
                    services.AddSingleton<IAntennaRotatorClient, AltAzimuthClient>();
                    break;
                case AntennaRotatorType.Kolovrat:
                    services.AddSingleton<IAntennaRotatorClient, KolovratClient>();
                    break;
                case AntennaRotatorType.RotorRAK:
                    services.AddSingleton<IAntennaRotatorClient, RotorRAKClient>();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            services.AddSingleton<IAntennaRotatorController, AntennaRotatorController>();
        } else {
            services.AddSingleton<IAntennaRotatorClient, EmptyAntennaRotatorClient>();
            services.AddSingleton<IAntennaRotatorController, EmptyAntennaRotatorController>();
        }

        var supervisorSettings = configuration.GetSection(SupervisorSettingsKey).Get<SupervisorSettings>();
        if (!string.IsNullOrEmpty(supervisorSettings?.IpAddress)) {
            services.AddSingleton<ISupervisorClient, SupervisorClient>();
            services.AddSingleton<ISupervisorController, SupervisorController>();
        } else {
            services.AddSingleton<ISupervisorController, EmptySupervisorController>();
        }

        var positionerSettings = configuration.GetSection(PositionerSettingsKey).Get<PositionerSettings>();
        if (!string.IsNullOrEmpty(positionerSettings?.IpAddress)
            && generalSettings?.StationType != StationType.ArchontT) {
            services.AddSingleton<IPositionerClient, InfoVectorClient>();
            services.AddSingleton<IPositionerController, PositionerController>();
        } else {
            services.AddSingleton<IPositionerController, EmptyPositionerController>();
        }

        services.AddSingleton<IGnssRecordingService, GnssRecordingService>();
        services.AddSingleton<IIQWriterFactory, IQWriterFactory>();
        services.AddSingleton<IPdwWriterFactory, PdwWriterFactory>();
        services.AddSingleton<IBandsHistogramService, BandsHistogramService>();
        services.AddSingleton<ITenetaClient, TenetaClient>();
        services.AddSingleton<IAntennaAngleControlService, AntennaAngleControlService>();
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddTransient<IDataClientFactory, DataClientFactory>();
        services.AddTransient<IDataAccessManager, DataAccessManager>();
        services.AddSingleton<IDataAccessManagerFactory, DataAccessManagerFactory>();
        services.AddSingleton<IReceiverService, ReceiverService>();
        services.AddSingleton<IPositionerService, PositionerService>();
        services.AddTransient<IPdwPositionOverrideService, PdwPositionOverrideService>();
        services.AddTransient<PdwPositionOverrideService>();
        services.AddTransient<EmptyPdwPositionOverrideService>();
        services.AddSingleton<IFileReceiver, FileReceiver>();
        services.AddSingleton<IMapObjectService, MapObjectService>();
        services.AddSingleton<IMessageLogger, RemoteMessageLogger>();
        services.AddSingleton<IndicatorAggregator>();
        services.AddSingleton<IRetryProvider, RetryProvider>();
        services.AddSingleton<IEndpointsTimeoutAggregationService, EndpointsTimeoutAggregationService>();
        services.AddSingleton<IStreamingService, StreamingService>();
       
        AddMqtt(services);
        AddControllers(services);
        AddStreaming(services);
        AddClusterization(services);
        AddTdoa(services);
        AddHostedService(services);
    }

    private static AppDataPathService
        SetupAppDataPathService(IServiceCollection services, IConfiguration configuration) {
        var appDataPathConfigSection = configuration.GetSection(AppDataPathSettings.Key);
        var appDataPathSettings = appDataPathConfigSection.Get<AppDataPathSettings>();
        var appDataPathOptions = Options.Create(appDataPathSettings!);
        services.AddSingleton(appDataPathOptions);
        var appDataService = new AppDataPathService(appDataPathOptions);
        services.AddSingleton(appDataService);
        if (appDataService.IsPortableModeEnabled && !Directory.Exists(appDataService.PortablePath)) {
            Directory.CreateDirectory(appDataService.PortablePath);
        }

        return appDataService;
    }

    private static void AddMqtt(IServiceCollection services) {
        services.SetupMqtt();
        services.AddSingleton<INotificationClient, NotificationClient>();
        services.AddSingleton<IRemoteClientFactory, RemoteClientFactoryFactory>();
    }

    private static void AddHostedService(IServiceCollection services) {
        services.AddHostedService<BrokerHostService>();
        services.AddHostedService<GracefullyShutdownHostService>();
    }

    private static void AddTdoa(IServiceCollection services) {
        services.AddSingleton<PtoaPdwExporter>();
        services.AddSingleton<IPtoaAlgorithm, PtoaAlgorithm>();
        services.AddSingleton<IPtoaAlgorithmFactory, PtoaAlgorithmFactory>();
        services.AddSingleton<IAoaAlgorithmFactory, AoaAlgorithmFactory>();
        services.AddSingleton<ITdoaMapSimulator, TdoaMapSimulator>();
        services.AddSingleton<IPtoaService, PtoaService>();
    }

    private static void AddControllers(IServiceCollection services) {
        services.AddSingleton<SystemControlController>();
        services.AddSingleton<ISystemControlController>(provider =>
            provider.GetRequiredService<SystemControlController>());
        services.AddSingleton<IBaseController>(provider =>
            provider.GetRequiredService<SystemControlController>());

        services.AddSingleton<PositionerController>();
        services.AddSingleton<IBaseController>(provider =>
            provider.GetRequiredService<PositionerController>());

        services.AddSingleton<AntennaRotatorController>();
        services.AddSingleton<IBaseController>(provider =>
            provider.GetRequiredService<AntennaRotatorController>());

        services.AddSingleton<DatabaseController>();
        services.AddSingleton<IDatabaseController>(provider =>
            provider.GetRequiredService<DatabaseController>());
        services.AddSingleton<IBaseController>(provider =>
            provider.GetRequiredService<DatabaseController>());

        services.AddSingleton<ReceiverController>();
        services.AddSingleton<IReceiverController>(provider => provider.GetRequiredService<ReceiverController>());
        services.AddSingleton<IBaseController>(provider => provider.GetRequiredService<ReceiverController>());

        services.AddSingleton<DataStreamingController>();
        services.AddSingleton<IDataStreamingController>(provider =>
            provider.GetRequiredService<DataStreamingController>());
        services.AddSingleton<IBaseController>(provider => provider.GetRequiredService<DataStreamingController>());

        services.AddSingleton<ManualPositionController>();
        services.AddSingleton<IManualPositionController>(provider =>
            provider.GetRequiredService<ManualPositionController>());
        services.AddSingleton<IBaseController>(provider => provider.GetRequiredService<ManualPositionController>());

        services.AddSingleton<LinkingController>();
        services.AddSingleton<ILinkingController>(provider =>
            provider.GetRequiredService<LinkingController>());
        services.AddSingleton<IBaseController>(provider => provider.GetRequiredService<LinkingController>());

        services.AddSingleton<TdoaController>();
        services.AddSingleton<ITdoaController>(provider => provider.GetRequiredService<TdoaController>());
        services.AddSingleton<IBaseController>(provider => provider.GetRequiredService<TdoaController>());
    }

    private static void AddStreaming(IServiceCollection services) {
        services.AddSingleton<IListenerFactory, ListenerFactory>();

        services.AddSingleton<ISpectrumStreamingService, SpectrumStreamingService>();
        services.AddSingleton<IThresholdStreamingService, ThresholdStreamingService>();
        services.AddSingleton<IPersistenceSpectrumStreamingService, PersistenceSpectrumStreamingService>();
        services.AddSingleton<IPdwStreamingService, PdwStreamingService>();

        services.AddSingleton<IClusterPdwStreamingServiceFactory, ClusterPdwStreamingServiceFactory>();
        services.AddTransient<IClusterPdwStreamingService, ClusterPdwStreamingService>();
        services.AddSingleton<IClusterPdwStreamingReceivingClientFactory, ClusterPdwStreamingReceivingClientFactory>();
        services.AddTransient<IClusterPdwStreamingReceivingClient, ClusterPdwStreamingReceivingClient>();
        services.AddSingleton<IClusterPdwRepeaterServiceFactory, ClusterPdwRepeaterServiceFactory>();
        services.AddTransient<IClusterPdwRepeaterService, ClusterPdwRepeaterService>();

        services.AddSingleton<ISpectrumRepeaterService, SpectrumRepeaterService>();
        services.AddSingleton<ISpectrumReceivingClient, SpectrumReceivingClient>();
        services.AddSingleton<IPdwRepeaterService, PdwRepeaterService>();
        services.AddSingleton<IIQRepeaterService, IQRepeaterService>();
        services.AddSingleton<IIQWithTimestampRepeaterService, IQWithTimestampRepeaterService>();
        services.AddSingleton<IIQStreamingService, IQStreamingService>();
        services.AddSingleton<IIQWithTimestampStreamingService, IQWithTimestampStreamingService>();
        services.AddSingleton<IIQReceivingClient, IQReceivingClient>();
        services.AddSingleton<IIQWithTimestampReceivingClient, IQWithTimestampReceivingClient>();
        services.AddSingleton<ISpectrumReceivingClient, SpectrumReceivingClient>();
        services.AddTransient<IPdwReceivingClient, PdwReceivingClient>();
    }

    private static void AddClusterization(IServiceCollection services) {
        services.AddSingleton<IPdwDeinterleaver, PdwDeinterleaver>();
        services.AddTransient<IClusteringProvider<PdwCluster>, ParallelClusteringProvider>();
        services.AddTransient<IRadarAssociationService, RadarAssociationService>();
        services.AddSingleton<IClusterizationService, ClusterizationService>();
        services.AddSingleton<IClusterGroupingService, ClusterGroupingService>();
        services.AddTransient<IClusterizationFlowFactory, ClusterizationFlowFactory>();
    }

    private static void SetWorkDirectories() {
        var commonAppDataPath = AppDataPathService.GetCommonAppDataPath();
        var userAppDataPath = AppDataPathService.GetUserAppDataPath();
        var userContentPath = AppDataPathService.GetUserContentPath();

        if (!Directory.Exists(commonAppDataPath)) {
            Directory.CreateDirectory(userAppDataPath);
        }

        if (!Directory.Exists(userAppDataPath)) {
            Directory.CreateDirectory(userAppDataPath);
        }

        if (!Directory.Exists(userContentPath)) {
            Directory.CreateDirectory(userContentPath);
        }
    }

    private static void SetCulture() {
        var cultureName = Thread.CurrentThread.CurrentCulture.Name;
        var culture = new CultureInfo(cultureName) { NumberFormat = { NumberGroupSeparator = " " } };
        Thread.CurrentThread.CurrentCulture = culture;
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        if (e.ExceptionObject is Exception exception) {
            _logger?.LogError(exception, "CurrentDomainUnhandledException");
            _messageLogger?.AddMessage(MessageCategory.System, $"{exception.Message}", MessageLevel.Error);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) {
        var ex = e.Exception.InnerException ?? e.Exception;
        _logger?.LogError(ex, "UnobservedTaskException");
        _messageLogger?.AddMessage(MessageCategory.System, $"{ex.Message}", MessageLevel.Error);
    }

    private static void ApplyMigrations() {
        using var scope = _host!.Services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetRequiredService<ElintDbContext>();
        dbContext.ApplyMigrations(_logger);
    }

    private static string? GetVersion() {
        var assembly = Assembly.GetExecutingAssembly();
        return FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
    }
}
