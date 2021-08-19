using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Transport.Mqtt;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZXing;
using static IotEdgeModule1.VisionResponse;

namespace IotEdgeModule1
{
    class Program
    {
        static int counter;
        static string visionItemName;
        static bool shoppingSessionStart = false;
        static string virtualBasketNumber = "001";
        static List<string> virtualBasket = new List<string>();

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


            await StartCameraStream(ioTHubModuleClient, shoppingSessionStart);
            //await StartCameraStreamForPredictionProduct(ioTHubModuleClient);
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

        private static async Task StartCameraStream(object userContext, bool shoppingSessionStart)
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
                // Detect barcode to start shopping session
                while (!shoppingSessionStart)
                {
                    capture.Read(frame);
                    Console.WriteLine("Start read frame");

                    if (frame.Empty())
                    {
                        Console.WriteLine("empty frame");
                        break;
                    }

                    // Read barcode to start a shopping session
                    var reader = new BarcodeReader();

                    var readerResult = reader.Decode(frame.ToBitmap());

                    if (readerResult != null && readerResult.BarcodeFormat != BarcodeFormat.QR_CODE)
                    {
                        var rewardsCardNumber = readerResult.Text;

                        var sessionStartMsg = $":rewardsleaf:*{rewardsCardNumber}* start shopping on virtual basket - {virtualBasketNumber}";

                        var msgBytes = Encoding.ASCII.GetBytes(sessionStartMsg);

                        using var pipeMessage = new Message(msgBytes);

                        await moduleClient.SendEventAsync("output1", pipeMessage);

                        shoppingSessionStart = true;
                    }
                }

                // Detect product
                while (shoppingSessionStart)
                {
                    capture.Read(frame);
                    Console.WriteLine("Start read frame");

                    if (frame.Empty())
                    {
                        Console.WriteLine("empty frame");
                        break;
                    }

                    // Read QR code to start a checkout session
                    var qrReader = new BarcodeReader();

                    var qrReaderResult = qrReader.Decode(frame.ToBitmap());

                    if (qrReaderResult != null && qrReaderResult.BarcodeFormat == BarcodeFormat.QR_CODE)
                    {
                        var b = qrReaderResult.BarcodeFormat.ToString();

                        var total = virtualBasket.Count * 10;

                        var sessionStartMsg = $"Checkout completed. Total paid: *${total:N}* Thank you for shopping in Woolies~ See ya next time~ :smile_cat:";

                        var msgBytes = Encoding.ASCII.GetBytes(sessionStartMsg);

                        using var pipeMessage = new Message(msgBytes);

                        await moduleClient.SendEventAsync("output1", pipeMessage);

                        // Reset session
                        shoppingSessionStart = false;
                        visionItemName = string.Empty;
                        virtualBasket.Clear();
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
                                var productInBasketMsg = $"Customer put a {SlackMessageConverter(highestProableItem.TagName)} in virtual basket ({virtualBasketNumber})";

                                var tagBytes = Encoding.ASCII.GetBytes(productInBasketMsg);

                                using var pipeMessage = new Message(tagBytes);

                                if (visionItemName != highestProableItem.TagName)
                                {
                                    await moduleClient.SendEventAsync("output1", pipeMessage);

                                    visionItemName = highestProableItem.TagName;

                                    virtualBasket.Add(highestProableItem.TagName);
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
        }

        private static async Task StartCameraStreamForPredictionProduct(object userContext)
        {
            Console.WriteLine("Open a video capture");
            var capture = new VideoCapture(0);
            
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

        private static string SlackMessageConverter(string content)
        {
            return content switch
            {
                "apple" => ":apple:",
                "Banana" => ":bananadance:",
                _ => content
            };
        }
    }
}
