using IotEdgeModule1.Model;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Transport.Mqtt;
using Microsoft.Azure.Devices.Shared;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZXing;
using static IotEdgeModule1.Model.VirtualBasket;
using static IotEdgeModule1.Model.VisionResponse;

namespace IotEdgeModule1
{
    class Program
    {
        static string visionItemName;
        static readonly bool shoppingSessionStart = false;
        static readonly string basketDeviceNumber = "001";
        readonly static VirtualBasket virtualBasket = new VirtualBasket();
        static bool enableCameraStream = false;

        private static readonly HttpClient _visionClient = GetVisionClient();

        static void Main()
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

            var moduleTwin = await ioTHubModuleClient.GetTwinAsync();

            var desiredProps = moduleTwin.Properties.Desired;

            if (desiredProps.Contains("EnableCameraStream"))
                enableCameraStream = desiredProps["EnableCameraStream"];

            // Listen desired props change
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertyChanged, ioTHubModuleClient);

            // Start Camera stream
            await StartCameraStream(ioTHubModuleClient, shoppingSessionStart, enableCameraStream);
        }

        private static async Task StartCameraStream(object userContext, bool shoppingSessionStart, bool enableCameraStream)
        {
            if (!(userContext is ModuleClient moduleClient))
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            while (true)
            {
                if (enableCameraStream)
                {
                    Console.WriteLine("Open a video capture");
                    var capture = new VideoCapture(0);

                    Console.WriteLine("Start a frame");
                    using var frame = new Mat();

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

                            //var sessionStartMsg = $":rewardsleaf:*{rewardsCardNumber}* start shopping on virtual basket - {virtualBasketNumber}";

                            var payload = new { rewardsCardNumber };

                            var msgBytes = Encoding.ASCII.GetBytes(JsonSerializer.Serialize(payload));

                            using var pipeMessage = new Message(msgBytes);

                            pipeMessage.Properties.Add("ShopperEvent", "CardScanned");
                            pipeMessage.Properties.Add("EdgeDevice", basketDeviceNumber);

                            await moduleClient.SendEventAsync("output1", pipeMessage);

                            shoppingSessionStart = true;

                            Console.Beep();
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
                            //var total = virtualBasket.Count * 10;

                            //var sessionStartMsg = $"Checkout completed. Total paid: *${total:N}* Thank you for shopping in Woolies~ See ya next time~ :smile_cat:";
                            var payload = JsonSerializer.Serialize(virtualBasket);

                            var msgBytes = Encoding.ASCII.GetBytes(payload);

                            using var pipeMessage = new Message(msgBytes);

                            pipeMessage.Properties.Add("ShopperEvent", "CheckoutScanned");
                            pipeMessage.Properties.Add("EdgeDevice", basketDeviceNumber);

                            await moduleClient.SendEventAsync("output1", pipeMessage);

                            // Reset session
                            shoppingSessionStart = false;
                            visionItemName = string.Empty;
                            virtualBasket.BasketProducts.Clear();
                            Console.Beep();
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

                                if (highestProableItem != null && visionItemName != highestProableItem.TagName)
                                {

                                    //var productInBasketMsg = $"Customer put a {SlackMessageConverter(highestProableItem.TagName)} in virtual basket ({virtualBasketNumber})";

                                    var basketProduct = ProductCodeConverter(highestProableItem.TagName);

                                    var payload = JsonSerializer.Serialize(basketProduct);

                                    var msgBytes = Encoding.ASCII.GetBytes(payload);

                                    using var pipeMessage = new Message(msgBytes);

                                    pipeMessage.Properties.Add("ShopperEvent", "ProductScanned");
                                    pipeMessage.Properties.Add("EdgeDevice", basketDeviceNumber);

                                    await moduleClient.SendEventAsync("output1", pipeMessage);

                                    // Throttle 
                                    visionItemName = highestProableItem.TagName;

                                    var existingProduct = virtualBasket.BasketProducts.Where(p => p.Stockcode == basketProduct.Stockcode).FirstOrDefault();

                                    if (existingProduct != null)
                                    {
                                        existingProduct.Quantity += basketProduct.Quantity;
                                    }
                                    else
                                    {
                                        virtualBasket.BasketProducts.Add(basketProduct);
                                    }

                                    Console.WriteLine("Message sent");
                                    Console.Beep();
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("vision api fails");
                        }
                    } 
                }
                else
                {
                    Console.WriteLine("DisableCameraStream");
                    // Do nothing to prevent app restart over and over again
                }
            }      
        }

        private static async Task OnDesiredPropertyChanged(TwinCollection desiredProperties, object userContext)
        {
            if (!(userContext is ModuleClient moduleClient))
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            if (desiredProperties.Contains("EnableCameraStream"))
            {
                enableCameraStream = desiredProperties["EnableCameraStream"];
            }

            await Task.CompletedTask;
        }
        private static HttpClient GetVisionClient()
        {
            var visionClient = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:80/")
            };

            return visionClient;
        }

        private static BasketProduct ProductCodeConverter(string content)
        {
            return content switch
            {
                "apple" => new BasketProduct { 
                    Stockcode = "105919",
                    ProductName = "Pink Lady Apple",
                    Quantity = 1
                },
                "Banana" => new BasketProduct
                {
                    Stockcode = "133211",
                    ProductName = "Cavendish Banana",
                    Quantity = 1
                },
                _ => null
            };
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
