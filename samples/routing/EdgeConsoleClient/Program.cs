using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Hyperledger.Aries.Agents;
using Hyperledger.Aries.Features.DidExchange;
using Hyperledger.Aries;
using Hyperledger.Aries.Routing;
using Hyperledger.Aries.Utils;

namespace EdgeConsoleClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));

            //var host1 = CreateHostBuilder("Edge1").Build();
            var host2 = CreateHostBuilder("MyTest").Build();

            try
            {
                //await host1.StartAsync();
                await host2.StartAsync();

                //var context1 = await host1.Services.GetRequiredService<IAgentProvider>().GetContextAsync();
                var context2 = await host2.Services.GetRequiredService<IAgentProvider>().GetContextAsync();

                //var (invitation, record1) = await host1.Services.GetRequiredService<IConnectionService>().CreateInvitationAsync(context1, new InviteConfiguration { AutoAcceptConnection = true });
                var invitation = MessageUtils.DecodeMessageFromUrlFormat<ConnectionInvitationMessage>(@"http://15.206.165.79/?c_i=eyJsYWJlbCI6Ik15IEFnZW5jeSIsImltYWdlVXJsIjpudWxsLCJzZXJ2aWNlRW5kcG9pbnQiOiJodHRwOi8vMTUuMjA2LjE2NS43OS8iLCJyb3V0aW5nS2V5cyI6WyJ5THZFNnJuckNKWG5iYzNOUXFhcWdQazJHUmtDd2p1dmJFR3gxOFh5Y0JHIl0sInJlY2lwaWVudEtleXMiOlsiMjVGV1NQZG5kSno4RlIyVE1IQmZmdnlKaHFIVnlrSFNHRzNFbmN1WEdjRmEiXSwiQGlkIjoiY2UyMmJkZWYtYTA3Zi00NGY2LThiOWMtNWViNDIzZTFkYjIxIiwiQHR5cGUiOiJkaWQ6c292OkJ6Q2JzTlloTXJqSGlxWkRUVUFTSGc7c3BlYy9jb25uZWN0aW9ucy8xLjAvaW52aXRhdGlvbiJ9");
                var (request, record2) = await host2.Services.GetRequiredService<IConnectionService>().CreateRequestAsync(context2, invitation);
                await host2.Services.GetRequiredService<IMessageService>().SendAsync(context2.Wallet, request, record2);

                //await host1.Services.GetRequiredService<IEdgeClientService>().FetchInboxAsync(context1);
                await host2.Services.GetRequiredService<IEdgeClientService>().FetchInboxAsync(context2);

                //record1 = await host1.Services.GetRequiredService<IConnectionService>().GetAsync(context1, record1.Id);
                record2 = await host2.Services.GetRequiredService<IConnectionService>().GetAsync(context2, record2.Id);

                //await host1.Services.GetRequiredService<IEdgeClientService>().AddDeviceAsync(context1, new AddDeviceInfoMessage { DeviceId = "123", DeviceVendor = "Apple" });

                //Console.WriteLine($"Record1 is {record1.State}, Record2 is {record2.State}");
            }
            catch(Exception e)
            {
                Console.WriteLine(e);
            }

            Console.WriteLine("Connected");
        }

        public static IHostBuilder CreateHostBuilder(string walletId) =>
            Host.CreateDefaultBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddAriesFramework(builder =>
                    {
                        builder.RegisterEdgeAgent(options =>
                        {
                            options.EndpointUri = "http://localhost:5000";
                            options.WalletConfiguration.Id = walletId;
                            options.AgentName = walletId;
                        });
                    });
                });
    }
}
