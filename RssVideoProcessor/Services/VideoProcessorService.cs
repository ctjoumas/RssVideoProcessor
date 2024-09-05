namespace RssVideoProcessor.Services
{
    using global::RssVideoProcessor.Util;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    internal class VideoProcessorService
    {
        private const string AzureResourceManager = "https://management.azure.com";

        private const string ApiUrl = "https://api.videoindexer.ai";

        // Connection string to the storage account
        //private static string StorageAccountConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        //private static string ContainerName = Environment.GetEnvironmentVariable("ContainerName");

        private static string FunctionCallbackUrl = Environment.GetEnvironmentVariable("FunctionCallbackUrl");


        /// <summary>
        /// Processes the video by uploading it to the Video Indexer.
        /// </summary>
        /// <param name="videoUrl">The URL to the video</param>
        /// <param name="log"></param>
        /// <returns></returns>
        /*private async Task ProcessVideo(Uri videoUri, ILogger logger)
        {
            // Create the http client
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);

            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(logger);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, logger);

            // Upload the video
            await videoIndexerResourceProviderClient.UploadVideo(name, uri, accountLocation, accountId, accountAccessToken, client, logger);
        }*/        
    }
}