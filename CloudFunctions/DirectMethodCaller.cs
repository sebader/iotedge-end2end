using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Devices;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Edge.End2End
{
    public static class DirectMethodCaller
    {

        private static IConfigurationRoot config = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

        // AppInsights TelemetryClient
        private static TelemetryClient telemetry = new TelemetryClient();

        private static ServiceClient _iothubServiceClient = ServiceClient.CreateFromConnectionString(config["iothubowner_cs"]);
        private const string METHOD_NAME = "NewMessageRequest";

        /// <summary>
        /// Function that calls a Direct Method on one or more Edge modules
        /// Direct Method name: NewMessageRequest
        /// </summary>
        /// <param name="myTimer"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        [FunctionName("DirectMethodCaller")]
        public static async Task Run([TimerTrigger("0 */2 * * * *", RunOnStartup = false)]TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"DirectMethodCaller function executed at: {DateTime.Now}");

            // Get device/modules from the config, which the Function should call the direct method on
            // Multiple destinations can be supplied with comma-separated
            var destinations = config["destinationmodules"];
            var destinationModules = destinations.Split(',');

            var methodRequest = new CloudToDeviceMethod(METHOD_NAME, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            var correlationId = Guid.NewGuid().ToString();
            var payloadJson = "{\"correlationId\": \"" + correlationId + "\", \"text\": \"End2End test message with correlationId=" + correlationId + "\"}";

            methodRequest.SetPayloadJson(payloadJson);
            foreach (var destination in destinationModules)
            {
                var parts = destination.Split('/');
                var device = parts[0];
                var module = parts[1];

                var properties = new Dictionary<string, string> { { "correlationId", correlationId } };
                telemetry.TrackEvent("10-StartMethodInvocation", properties);
                try
                {
                    log.LogInformation($"Invoking method {METHOD_NAME} on module {destination}. CorrelationId={correlationId}");
                    // Invoke direct method
                    var result = await _iothubServiceClient.InvokeDeviceMethodAsync(device, module, methodRequest).ConfigureAwait(false);

                    properties.Add("MethodReturnCode", $"{result.Status}");
                    if (IsSuccessStatusCode(result.Status))
                    {
                        telemetry.TrackEvent("11-SuccessfulMethodInvocation", properties);
                        log.LogInformation($"[{destination}] Successful direct method call result code={result.Status}");
                    }
                    else
                    {
                        telemetry.TrackEvent("15-UnsuccessfulMethodInvocation", properties);
                        log.LogWarning($"[{destination}] Unsuccessful direct method call result code={result.Status}");
                    }
                }
                catch (Exception e)
                {
                    log.LogError(e, $"[{destination}] Exeception on direct method call");
                }
            }
        }
        private static bool IsSuccessStatusCode(int statusCode)
        {
            return (statusCode >= 200) && (statusCode <= 299);
        }
    }
}