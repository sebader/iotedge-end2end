namespace DirectMethodReceiver
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Newtonsoft.Json;
    using Serilog;

    class Program
    {
        // AppInsights TelemetryClient
        private static TelemetryClient telemetry = new TelemetryClient();

        private static int counter = 0;

        public static int Main() => MainAsync().Result;

        static async Task<int> MainAsync()
        {
            InitLogging();
            Log.Information($"Module {Environment.GetEnvironmentVariable("IOTEDGE_MODULEID")} starting up...");
            var moduleClient = await Init();

            // Register direct method handlers
            await moduleClient.SetMethodHandlerAsync("NewMessageRequest", NewMessageRequest, moduleClient);

            await moduleClient.SetMethodDefaultHandlerAsync(DefaultMethodHandler, moduleClient);

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            await WhenCancelled(cts.Token);

            return 0;
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient
        /// </summary>
        static async Task<ModuleClient> Init()
        {
            var transportType = TransportType.Mqtt_Tcp_Only;
            string transportProtocol = Environment.GetEnvironmentVariable("TransportProtocol");

            // The way the module connects to the EdgeHub can be controlled via the env variable. Either MQTT or AMQP
            if (!string.IsNullOrEmpty(transportProtocol))
            {
                switch (transportProtocol.ToUpper())
                {
                    case "AMQP":
                        transportType = TransportType.Amqp_Tcp_Only;
                        break;
                    case "MQTT":
                        transportType = TransportType.Mqtt_Tcp_Only;
                        break;
                    default:
                        // Anything else: use default of MQTT
                        Log.Warning($"Ignoring unknown TransportProtocol={transportProtocol}. Using default={transportType}");
                        break;
                }
            }

            // Open a connection to the Edge runtime
            ModuleClient moduleClient = await ModuleClient.CreateFromEnvironmentAsync(transportType);
            await moduleClient.OpenAsync();

            moduleClient.SetConnectionStatusChangesHandler(ConnectionStatusHandler);

            Log.Information($"Edge Hub module client initialized using {transportType}");

            return moduleClient;
        }

        /// <summary>
        /// Callback for whenever the connection status changes
        /// Mostly we just log the new status and the reason. 
        /// But for some disconnects we need to handle them here differently for our module to recover
        /// </summary>
        /// <param name="status"></param>
        /// <param name="reason"></param>
        private static void ConnectionStatusHandler(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            Log.Information($"Module connection changed. New status={status.ToString()} Reason={reason.ToString()}");

            // Sometimes the connection can not be recovered if it is in either of those states.
            // To solve this, we exit the module. The Edge Agent will then restart it (retrying with backoff)
            if (reason == ConnectionStatusChangeReason.Retry_Expired)
            {
                Log.Error($"Connection can not be re-established. Exiting module");
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Default method handler for any method calls which are not implemented
        /// </summary>
        /// <param name="methodRequest"></param>
        /// <param name="userContext"></param>
        /// <returns></returns>
        private static Task<MethodResponse> DefaultMethodHandler(MethodRequest methodRequest, object userContext)
        {
            Log.Information($"Received method invocation for non-existing method {methodRequest.Name}. Returning 404.");
            var result = new MethodResponsePayload() { ModuleResponse = $"Method {methodRequest.Name} not implemented" };
            var outResult = JsonConvert.SerializeObject(result, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(outResult), 404));
        }

        /// <summary>
        /// Handler for NewMessageRequests
        /// Creates a new IoT Message based on the input from the method request and sends it to the Edge Hub
        /// </summary>
        /// <param name="methodRequest"></param>
        /// <param name="userContext"></param>
        /// <returns></returns>
        private static async Task<MethodResponse> NewMessageRequest(MethodRequest methodRequest, object userContext)
        {
            int counterValue = Interlocked.Increment(ref counter);
            var moduleClient = userContext as ModuleClient;

            int resultCode = 200;
            MethodResponsePayload result;

            var request = JsonConvert.DeserializeObject<MethodRequestPayload>(methodRequest.DataAsJson);

             var telemetryProperties = new Dictionary<string, string>
            {
                { "correlationId", request.CorrelationId },
                { "edgeModuleId", Environment.GetEnvironmentVariable("IOTEDGE_MODULEID") },
                { "timestamp", DateTime.UtcNow.ToString("o") }
            };

            Log.Information($"NewMessageRequest method invocation received. Count={counterValue}. CorrelationId={request.CorrelationId}");
            telemetry.TrackEvent("20-ReceivedDirectMethodRequest", telemetryProperties);

            var message = new Message(Encoding.UTF8.GetBytes(request.Text));
            message.ContentType = "application/json";
            message.ContentEncoding = "UTF-8";
            message.Properties.Add("correlationId", request.CorrelationId);
            message.Properties.Add("scope", "end2end");

            try
            {
                await moduleClient.SendEventAsync("output1", message);
                telemetry.TrackEvent("21-MessageSentToEdgeHub", telemetryProperties);
                Log.Information("Message sent successfully to Edge Hub");
                result = new MethodResponsePayload() { ModuleResponse = $"Message sent successfully to Edge Hub" };
            }
            catch (Exception e)
            {
                Log.Error(e, "Error during message sending to Edge Hub");
                telemetry.TrackEvent("25-ErrorMessageNotSentToEdgeHub", telemetryProperties);
                resultCode = 500;
                result = new MethodResponsePayload() { ModuleResponse = $"Message not sent to Edge Hub" };
            }
            var outResult = JsonConvert.SerializeObject(result);
            return new MethodResponse(Encoding.UTF8.GetBytes(outResult), resultCode);
        }

        /// <summary>
        /// Initialize logging using Serilog
        /// LogLevel can be controlled via RuntimeLogLevel env var
        /// </summary>
        private static void InitLogging()
        {
            LoggerConfiguration loggerConfiguration = new LoggerConfiguration();

            var logLevel = Environment.GetEnvironmentVariable("RuntimeLogLevel");
            logLevel = !string.IsNullOrEmpty(logLevel) ? logLevel.ToLower() : "info";

            // set the log level
            switch (logLevel)
            {
                case "fatal":
                    loggerConfiguration.MinimumLevel.Fatal();
                    break;
                case "error":
                    loggerConfiguration.MinimumLevel.Error();
                    break;
                case "warn":
                    loggerConfiguration.MinimumLevel.Warning();
                    break;
                case "info":
                    loggerConfiguration.MinimumLevel.Information();
                    break;
                case "debug":
                    loggerConfiguration.MinimumLevel.Debug();
                    break;
                case "verbose":
                    loggerConfiguration.MinimumLevel.Verbose();
                    break;
            }

            // set logging sinks
            loggerConfiguration.WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] - {Message}{NewLine}{Exception}");
            loggerConfiguration.Enrich.FromLogContext();
            Log.Logger = loggerConfiguration.CreateLogger();
            Log.Information($"Initializied logger with log level {logLevel}");
        }
    }

    /// <summary>
    /// Payload of direct method request
    /// </summary>
    class MethodRequestPayload
    {
        public string CorrelationId { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Payload of direct method response
    /// </summary>
    class MethodResponsePayload
    {
        public string ModuleResponse { get; set; } = null;
    }
}