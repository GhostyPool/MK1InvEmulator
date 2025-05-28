using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fluxzy;
using Fluxzy.Clients.Headers;
using Fluxzy.Clients.Mock;
using Fluxzy.Core;
using Fluxzy.Core.Breakpoints;
using Fluxzy.Misc.Streams;
using Fluxzy.Rules;
using Fluxzy.Rules.Actions;
using Fluxzy.Rules.Actions.HighLevelActions;
using Fluxzy.Rules.Filters;
using Fluxzy.Rules.Filters.RequestFilters;
using Action = Fluxzy.Rules.Action;

namespace Emulator
{
    internal class PacketHandling
    {

        private readonly FluxzySetting fluxzySettings;
        private readonly Proxy proxy;

        public Proxy Proxy => proxy;

        public PacketHandling(int port)
        {
            fluxzySettings = FluxzySetting.CreateDefault(IPAddress.Loopback, port);

            fluxzySettings
                .SetAutoInstallCertificate(true)
                .ConfigureRule()

                //Patch inv
                .WhenUriMatch("https://k1-api.wbagora.com/ssc/invoke/inventory_load", StringSelectorOperation.Exact)
                .Do(new PatchInv())

                //Patch kustomization responses
                .WhenUriMatch("https://k1-api.wbagora.com/ssc/invoke/inventory_kustomize_update_configuration", StringSelectorOperation.Exact)
                .Do(new PatchResponseKustomize())

                //Prevent mapmode and inventory updates
                .WhenAny(new AbsoluteUriFilter("https://k1-api.wbagora.com/ssc/invoke/atomic_mapmode_update", StringSelectorOperation.Exact),
                        new AbsoluteUriFilter("https://k1-api.wbagora.com/ssc/invoke/get_mapmode_progression", StringSelectorOperation.Exact),
                        new AbsoluteUriFilter("https://k1-api.wbagora.com/ssc/invoke/challenge_points_get_data", StringSelectorOperation.Exact))
                .Abort()

                .WhenUriMatch("https://k1-api.wbagora.com/ssc/invoke/inventory_update_experience", StringSelectorOperation.Exact)
                .Do(new PreventResponse());

            proxy = new Proxy(fluxzySettings);
        }

        public async Task startProxy(IPEndPoint clash)
        {
            proxy.Run();

            //Register as system proxy if needed
            var _ = await SystemProxyRegistrationHelper.Create(clash);
        }
    }

    internal class PatchResponseKustomize : Action
    {
        public override FilterScope ActionScope => FilterScope.RequestHeaderReceivedFromClient;

        public override string DefaultDescription => nameof(PatchResponseKustomize);

        public override async ValueTask InternalAlter(ExchangeContext context, Exchange? exchange, Connection? connection, FilterScope scope, BreakPointManager breakPointManager)
        {
            //Save request body
            var requestBodyStream = exchange.Request.Body;

            if (requestBodyStream != null)
            {
                var requestBody = await requestBodyStream.ToArrayGreedyAsync();

                //Debug save to disk
                await Debug.writeDebugFile("kustomize_request.bin", requestBody);

                InventoryContainer.LastRequest = HydraHelpers.decodeFromHydra(requestBody);
            }
            else
            {
                Debug.printError("ERROR: Kustomize request body is null");
                return;
            }

            //Generate patched response
            var patchedResponseKustomize = await InventoryContainer.createKustomizeResponse();

            if (patchedResponseKustomize != null)
            {
                await Debug.writeDebugFile("patched_response_kustomize.bin", patchedResponseKustomize);

                context.PreMadeResponse = MockedResponseContent.CreateFromByteArray(patchedResponseKustomize, 200, "application/x-ag-binary");
            }
            else
            {
                Debug.printError("ERROR: There was an error generating the patched response!");
                context.PreMadeResponse = null;
            }
        }
    }

    internal class PatchInv : Action
    {
        public override FilterScope ActionScope => FilterScope.ResponseHeaderReceivedFromRemote;

        public override string DefaultDescription => nameof(PatchInv);

        public override async ValueTask InternalAlter(ExchangeContext context, Exchange? exchange, Connection? connection, FilterScope scope, BreakPointManager breakPointManager)
        {
            var responseStream = exchange.Response.Body;

            if (responseStream != null)
            {
                //Save original inv
                var responseBody = await responseStream.ToArrayGreedyAsync();

                await Debug.writeDebugFile("inventory_user_original.bin", responseBody);

                Debug.debugLog("Original inventory saved!");

                //Sync lvl
                InventoryContainer.syncPlayerLvl(HydraHelpers.decodeFromHydra(responseBody));
                Debug.debugLog("Inventory patched!");

                context.RegisterResponseBodySubstitution(new PatchInvResponse());

                context.ResponseHeaderAlterations.Clear();
                context.ResponseHeaderAlterations.Add(new HeaderAlterationReplace("Content-Length", InventoryContainer.Inventory.Length.ToString(), true));
                context.ResponseHeaderAlterations.Add(new HeaderAlterationReplace("Content-Type", "application/x-ag-binary", true));
            }
            else
            {
                Debug.exitWithError("There was an error retrieving your inventory!");
            }
        }
    }

    internal class PatchInvResponse : IStreamSubstitution
    {
        public async ValueTask<Stream> Substitute(Stream originalStream)
        {
            //Drain body
            await originalStream.DrainAsync();

            return new MemoryStream(InventoryContainer.Inventory);
        }
    }

    internal class PreventResponse : Action
    {
        public override FilterScope ActionScope => FilterScope.ResponseHeaderReceivedFromRemote;

        public override string DefaultDescription => nameof(PreventResponse);

        public override ValueTask InternalAlter(ExchangeContext context, Exchange? exchange, Connection? connection, FilterScope scope, BreakPointManager breakPointManager)
        {

            context.RegisterResponseBodySubstitution(new EmptyResponse());

            context.ResponseHeaderAlterations.Clear();
            context.ResponseHeaderAlterations.Add(new HeaderAlterationReplace("Content-Length", "0", true));

            context.Abort = true;

            Debug.debugLog("Response drained for connection: " + exchange.FullUrl);

            return default;
        }
    }

    internal class EmptyResponse : IStreamSubstitution
    {
        public async ValueTask<Stream> Substitute(Stream originalStream)
        {
            await originalStream.DrainAsync();

            return new MemoryStream();
        }
    }
}
