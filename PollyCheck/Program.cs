using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System.Net;
using static PollyCheck.Proto.TestService;

namespace PollyChack
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // DI
            var services = new ServiceCollection();

            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug);
            });

            var serverErrors = new HttpStatusCode[] {
                HttpStatusCode.BadGateway,
                HttpStatusCode.GatewayTimeout,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.InternalServerError,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.RequestTimeout
            };

            var gRpcErrors = new StatusCode[] {
                StatusCode.DeadlineExceeded,
                StatusCode.Internal,
                StatusCode.NotFound,
                StatusCode.ResourceExhausted,
                StatusCode.Unavailable,
                StatusCode.Unknown
            };

            Func<HttpRequestMessage, IAsyncPolicy<HttpResponseMessage>> retryFunc = (request) =>
            {
                return Policy.HandleResult<HttpResponseMessage>(r =>
                {
                    var grpcStatus = StatusManager.GetStatusCode(r);
                    var httpStatusCode = r.StatusCode;

                    return (grpcStatus == null && serverErrors.Contains(httpStatusCode)) || // if the server send an error before gRPC pipeline
                           (httpStatusCode == HttpStatusCode.OK && gRpcErrors.Contains(grpcStatus.Value)); // if gRPC pipeline handled the request (gRPC always answers OK)
                })
                .WaitAndRetryAsync(3, (input) => TimeSpan.FromSeconds(3 + input), (result, timeSpan, retryCount, context) =>
                {
                    var grpcStatus = StatusManager.GetStatusCode(result.Result);
                    Console.WriteLine($"Request failed with {grpcStatus}. Retry");
                });
            };

            services.AddGrpcClient<TestServiceClient>(o =>
            {
                o.Address = new Uri("https://localhost:5001");
            }).AddPolicyHandler(retryFunc);

            var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<TestServiceClient>();

            try
            {
                var testClient = (await client.TestAsync(new Empty()));
            }
            catch (RpcException e)
            {
                Console.WriteLine(e.Message);
            }
        }

        public static class StatusManager
        {
            public static StatusCode? GetStatusCode(HttpResponseMessage response)
            {
                var headers = response.Headers;

                if (!headers.Contains("grpc-status") && response.StatusCode == HttpStatusCode.OK)
                    return StatusCode.OK;

                if (headers.Contains("grpc-status"))
                    return (StatusCode)int.Parse(headers.GetValues("grpc-status").First());

                return null;
            }
        }
    }
}


