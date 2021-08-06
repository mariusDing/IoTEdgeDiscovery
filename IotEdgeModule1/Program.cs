using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Transport.Mqtt;
using OpenCvSharp;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static IotEdgeModule1.VisionResponse;

namespace IotEdgeModule1
{
    class Program
    {
        static int counter;
        static string visionItemName;

        private static readonly HttpClient _visionClient = GetVisionClient();

        static void Main(string[] args)
        {
            Init().Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
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
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };

            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("Test module 1 is initialized.");

            // Register callback to be called when a message is received by the module
            //await ioTHubModuleClient.SetInputMessageHandlerAsync("input111", PipeMessage, ioTHubModuleClient);

            Console.WriteLine("Begin to start camera stream");
            await StartCameraStream(ioTHubModuleClient);
        }

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It just pipe the messages without any change.
        /// It prints all the incoming messages.
        /// </summary>
        static async Task<MessageResponse> PipeMessage(Message message, object userContext)
        {
            int counterValue = Interlocked.Increment(ref counter);

            var moduleClient = userContext as ModuleClient;
            if (moduleClient == null)
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            byte[] messageBytes = message.GetBytes();
            string messageString = Encoding.UTF8.GetString(messageBytes);
            Console.WriteLine($"Received message: {counterValue}, Body: [{messageString}]");

            if (!string.IsNullOrEmpty(messageString))
            {
                using (var pipeMessage = new Message(messageBytes))
                {
                    foreach (var prop in message.Properties)
                    {
                        pipeMessage.Properties.Add(prop.Key, prop.Value);
                    }
                    await moduleClient.SendEventAsync("output1", pipeMessage);

                    Console.WriteLine("Received message sent");
                }
            }
            return MessageResponse.Completed;
        }

        private static async Task StartCameraStream(object userContext)
        {
            Console.WriteLine("Open a video capture");
            var capture = new VideoCapture(1);
            
            Console.WriteLine("Start a frame");
            using var frame = new Mat();

            if (!(userContext is ModuleClient moduleClient))
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            while (true)
            {
                capture.Read(frame);
                Console.WriteLine("Start read frame");

                if (frame.Empty())
                {
                    Console.WriteLine("empty frame");
                    break;
                }


                var frameBytes = frame.ToBytes(".png");

                var httpContent = new ByteArrayContent(frameBytes);

                Console.WriteLine("start send to vision api");
                var response = await _visionClient.PostAsync("image", httpContent);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("start deserialize");
                    var result = JsonSerializer.Deserialize<VisionResponse>(content, new JsonSerializerOptions()
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }); ;

                    if (result.Predictions.Any())
                    {
                        var highestRate = 0;

                        VisionPrediction highestProableItem = null;

                        result.Predictions.ForEach(p => { 
                            if (p.Probability >= 1.0 && p.Probability > highestRate)
                            {
                                highestProableItem = p;
                            }
                        });

                        if (highestProableItem != null)
                        {
                            var tagBytes = Encoding.ASCII.GetBytes(highestProableItem.TagName);

                            using var pipeMessage = new Message(tagBytes);

                            if (visionItemName != highestProableItem.TagName)
                            {
                                await moduleClient.SendEventAsync("output1", pipeMessage);

                                visionItemName = highestProableItem.TagName;
                            }

                            Console.WriteLine("Message sent");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("vision api fails");
                }
            }
        }
        private static HttpClient GetVisionClient()
        {
            var visionClient = new HttpClient
            {
                BaseAddress = new Uri("http://127.0.0.1/")
            };

            return visionClient;
        }
    }
}
