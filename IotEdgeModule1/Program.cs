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
using System.Device.Gpio;
using System.Collections.Generic;

namespace IotEdgeModule1
{
    class Program
    {
        static string visionItemName;
        static readonly bool shoppingSessionStart = false;
        static readonly string basketDeviceNumber = "001";
        readonly static VirtualBasket virtualBasket = new VirtualBasket();
        static bool enableCameraStream = true;
        static bool enableGPIO = false;
        static int red = 3;
        static int yellow = 5;
        static int green = 7;
        static List<int> ledPins;
        static int frameRecord = 0;
        static int frameRecordMax = 60;


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

            if (desiredProps.Contains("FrameRecordMax"))
                frameRecordMax = desiredProps["FrameRecordMax"];

            // Listen desired props change
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertyChanged, ioTHubModuleClient);

            // Start Camera stream
            await StartCameraStream(ioTHubModuleClient, shoppingSessionStart, enableCameraStream);
        }

        private static async Task StartCameraStream(object userContext, bool shoppingSessionStart, bool enableCameraStream)
        {
            // Init GPIO
            Console.WriteLine("init GpIO");
            var controller = InitGpIO();

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

                        // Indicate light to yellow as ready
                        TurnOnLight(controller, yellow);

                        if (frame.Empty())
                        {
                            Console.WriteLine("empty frame");
                            break;
                        }
                        Console.WriteLine("frame read");
                        // Read barcode to start a shopping session
                        var reader = new BarcodeReader();
                        Console.WriteLine("start barcode reader");
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

                            // Indicate light to green as session start
                            TurnOnLight(controller, green);
                        }
                    }

                    // Detect product
                    while (shoppingSessionStart)
                    {
                        Console.WriteLine("Start read frame");
                        capture.Read(frame);
                        Console.WriteLine("Start read frame");
                        Interlocked.Increment(ref frameRecord);
                        Console.WriteLine($"frame {frameRecord}");


                        if (frame.Empty())
                        {
                            Console.WriteLine("empty frame");
                            break;
                        }

                        if (frameRecord >= frameRecordMax)
                        {
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

                                // Indicate light to yellow as session end
                                TurnOnLight(controller, yellow);

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

                                        // Green light flash
                                        LightFlash(controller, green);
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine("vision api fails");
                            }

                            Interlocked.Exchange(ref frameRecord, 0);
                        }
                    }

                    capture.Dispose();
                }
                else
                {
                    Console.WriteLine("CameraStream Disabled");
                    //TurnOnLight(controller, red);
                    Console.WriteLine("Turn on RED in CameraStream Disabled");
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
                BaseAddress = new Uri($"http://image-classifier-service:80/") // docker can't access 127.0.0.1
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

        private static GpioController InitGpIO()
        {
            GpioController controller = null;

            if (enableGPIO)
            {
                controller = new GpioController(PinNumberingScheme.Board);

                controller.OpenPin(red, PinMode.Output);
                controller.OpenPin(yellow, PinMode.Output);
                controller.OpenPin(green, PinMode.Output);

                ledPins = new List<int>() { red, yellow, green };

                Console.WriteLine("InitGpIO initialised.");

                TurnOnLight(controller, red, true);

                Console.WriteLine("Turn on RED at start");
            }


            return controller;
        }

        private static void TurnOnLight(GpioController controller, int lightPin, bool reset = false)
        {
            if (controller != null)
            {
                if (controller.Read(lightPin) == PinValue.Low || reset)
                {
                    ledPins.ForEach(p => {
                        if (p == lightPin)
                        {
                            controller.Write(p, PinValue.High);
                        }
                        else
                        {
                            controller.Write(p, PinValue.Low);
                        }
                    });
                }
            }
        }

        private static void LightFlash(GpioController controller, int lightPin, int flashCount = 1)
        {
            if (controller != null)
            {
                for (var i = 0; i < flashCount; i++)
                {
                    controller.Write(lightPin, PinValue.Low);
                    controller.Write(lightPin, PinValue.High);
                }
            }
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
